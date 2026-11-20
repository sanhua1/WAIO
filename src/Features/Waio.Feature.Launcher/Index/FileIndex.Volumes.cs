using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Waio.Feature.Launcher.Infrastructure.Ntfs;

namespace Waio.Feature.Launcher.Index;

/// <summary>正在索引的一个卷。字段只在索引线程上写;<see cref="Index"/>、<see cref="State"/>、<see cref="Filter"/> 搜索线程会读。</summary>
internal sealed class IndexedVolume(VolumeInfo info)
{
    public VolumeInfo Info { get; } = info;

    public volatile VolumeIndex? Index;
    public volatile VolumeState State = VolumeState.Waiting;
    public volatile FileFilter Filter = FileFilter.None;
    public SafeFileHandle? Handle;
    public string? Message;
    public long RecordsRead;
    public long RecordsTotal;
    public DateTimeOffset LastSave = DateTimeOffset.Now;
    public DateTimeOffset RetryAt;

    /// <summary>存盘文件名:按卷序列号区分。</summary>
    public string FileName => $"{Info.Serial:X8}.bin";

    public VolumeStatus Status() => new(
        Info.MountPath, Info.FileSystem, State, Index?.Entries ?? 0, Index?.MemoryBytes ?? 0, Index?.ScannedAt,
        RecordsRead, RecordsTotal, Message);

    /// <summary>扔掉索引和存盘文件,下一轮重新扫描(扫描期间旧索引不再可搜)。</summary>
    public void Discard(string file)
    {
        Index = null;
        State = VolumeState.Waiting;
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed partial class FileIndex
{
    /// <summary>USN 日志不存在时按这个大小创建(Windows fsutil 的默认值;Everything 的"Enable USN Journal"默认也会创建)。</summary>
    private const ulong JournalMaximumSize = 32 << 20;
    private const ulong JournalAllocationDelta = 8 << 20;

    private readonly byte[] _journalBuffer = new byte[1 << 20];
    private readonly List<UsnChange> _changes = [];
    private DateTimeOffset _lastProgress;

    private void Tick(IndexedVolume volume, CancellationToken cancellation)
    {
        switch (volume.State)
        {
            case VolumeState.Waiting:
                Prepare(volume, cancellation);
                break;
            case VolumeState.Ready:
                Poll(volume, cancellation);
                SaveIfDirty(volume, force: false);
                break;
            case VolumeState.Failed when DateTimeOffset.Now >= volume.RetryAt:
                volume.State = volume.Index is null ? VolumeState.Waiting : VolumeState.Ready; // 有索引的(读日志临时出错)不用重扫
                break;
        }
    }

    /// <summary>打开卷 → 读盘(日志对得上就直接用)→ 否则全盘扫描。</summary>
    private void Prepare(IndexedVolume volume, CancellationToken cancellation)
    {
        var root = volume.Info.MountPath;
        if (!volume.Info.IsNtfs)
        {
            volume.State = VolumeState.Unsupported;
            volume.Message = $"{volume.Info.FileSystem} 格式的磁盘暂不支持(只支持 NTFS)";
            Changed();
            return;
        }

        if (volume.Handle is null || volume.Handle.IsInvalid)
        {
            volume.Handle?.Dispose();
            volume.Handle = NtfsNative.OpenVolume(volume.Info.DevicePath, out var error);
            if (volume.Handle.IsInvalid)
            {
                volume.Handle = null;
                Fail(volume, error == 5 ? VolumeState.NeedsAdmin : VolumeState.Failed,
                    error == 5 ? "需要以管理员身份运行才能索引这个磁盘" : $"打开卷失败(错误 {error})");
                return;
            }
        }

        if (!TryJournal(volume, out var journal))
        {
            return;
        }

        var file = Path.Combine(_directory, volume.FileName);
        if (volume.Index is null && File.Exists(file))
        {
            volume.State = VolumeState.Loading;
            Changed();
            var watch = Stopwatch.StartNew();
            var loaded = VolumeIndex.Load(file, root, volume.Info.Serial, _home);
            if (loaded is not null && loaded.JournalId == journal.UsnJournalId
                && loaded.NextUsn >= journal.LowestValidUsn && loaded.NextUsn <= journal.NextUsn)
            {
                volume.Index = loaded;
                volume.Filter = FilterFor(volume);
                volume.State = VolumeState.Ready;
                volume.Message = null;
                _logger.LogInformation("文件索引:{Root} 读盘 {Count} 条,用时 {Elapsed} ms,接着读日志", root, loaded.Entries, watch.ElapsedMilliseconds);
                Changed();
                return;
            }

            _logger.LogInformation("文件索引:{Root} 的存盘对不上(日志换过或已经被覆盖),重新扫描", root);
        }

        Scan(volume, journal, cancellation);
    }

    /// <summary>全盘扫描:先记下日志位置,扫完后从这里补上扫描期间的变化(MAIO / Everything 的做法,避免漏事件)。</summary>
    private void Scan(IndexedVolume volume, NtfsNative.UsnJournalData journal, CancellationToken cancellation)
    {
        var root = volume.Info.MountPath;
        if (!NtfsNative.TryGetVolumeData(volume.Handle!, out var data))
        {
            Fail(volume, VolumeState.Failed, "读取磁盘信息失败");
            return;
        }

        var watch = Stopwatch.StartNew();
        var reader = new MftReader(volume.Handle!, data);
        volume.State = VolumeState.Scanning;
        volume.RecordsTotal = reader.RecordCount;
        volume.RecordsRead = 0;
        Changed();
        try
        {
            var index = new VolumeIndex(root, volume.Info.Serial, (int)Math.Min(int.MaxValue - 1, reader.RecordCount), _home)
            {
                JournalId = journal.UsnJournalId, NextUsn = journal.NextUsn, ScannedAt = DateTimeOffset.Now,
            };
            var builder = new MftIndexBuilder(index);
            reader.ReadAll(builder.Visit, read =>
            {
                volume.RecordsRead = read;
                if (DateTimeOffset.Now - _lastProgress > TimeSpan.FromMilliseconds(500))
                {
                    _lastProgress = DateTimeOffset.Now;
                    Changed();
                }
            }, cancellation);
            index.Derive();
            index.TrimExcess();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true); // 建库留下的几百 MB 临时数组立即还给系统
            volume.Index = index;
            volume.Filter = FilterFor(volume);
            volume.State = VolumeState.Ready;
            volume.Message = null;
            _logger.LogInformation(
                "文件索引:{Root} 扫描完成,{Count} 条,用时 {Elapsed:F1} s,约 {Memory} MB",
                root, index.Entries, watch.Elapsed.TotalSeconds, index.MemoryBytes >> 20);
            index.Dirty = true;
            SaveIfDirty(volume, force: true);
        }
        catch (OperationCanceledException)
        {
            volume.State = VolumeState.Waiting;
            throw;
        }
        catch (Exception ex) when (ex is IOException or OutOfMemoryException)
        {
            _logger.LogError(ex, "文件索引:{Root} 扫描失败", root);
            Fail(volume, VolumeState.Failed, $"扫描失败:{ex.Message}");
        }

        Changed();
    }

    /// <summary>读到最新为止的日志并应用。日志被覆盖了(太久没读)或换了日志 → 重新扫描。</summary>
    private void Poll(IndexedVolume volume, CancellationToken cancellation)
    {
        if (volume.Index is not { } index || volume.Handle is not { } handle)
        {
            return;
        }

        for (var round = 0; round < 64 && !cancellation.IsCancellationRequested; round++)
        {
            var bytes = NtfsNative.ReadJournal(handle, index.JournalId, index.NextUsn, _journalBuffer, out var error);
            if (bytes < 0)
            {
                if (error is NtfsNative.ErrorJournalEntryDeleted or NtfsNative.ErrorJournalNotActive or NtfsNative.ErrorJournalDeleteInProgress)
                {
                    _logger.LogWarning("文件索引:{Root} 的 USN 日志对不上了(错误 {Error}),重新扫描", volume.Info.MountPath, error);
                    volume.Discard(Path.Combine(_directory, volume.FileName));
                }
                else
                {
                    Fail(volume, VolumeState.Failed, $"读取文件变动记录失败(错误 {error}),{RetryDelay.TotalSeconds:F0} 秒后重试");
                }

                return;
            }

            _changes.Clear();
            var next = UsnRecords.Parse(_journalBuffer.AsSpan(0, bytes), _changes);
            if (_changes.Count > 0)
            {
                try
                {
                    index.Apply(_changes, reference =>
                        NtfsNative.TryGetInfoById(handle, reference, out var size, out var lastWrite, out var attributes)
                            ? new FileDetails(size, lastWrite, attributes)
                            : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 跳过这一批、继续往前读:否则同一批每秒失败一次,索引永远停在这里
                    _logger.LogError(ex, "文件索引:{Root} 有一批日志({Count} 条)没能应用,已跳过", volume.Info.MountPath, _changes.Count);
                }
            }

            if (next > index.NextUsn)
            {
                index.NextUsn = next;
            }

            if (bytes < _journalBuffer.Length / 2)
            {
                break; // 读到头了
            }
        }
    }

    private bool TryJournal(IndexedVolume volume, out NtfsNative.UsnJournalData journal)
    {
        if (NtfsNative.TryQueryJournal(volume.Handle!, out journal, out var error))
        {
            return true;
        }

        if (error == NtfsNative.ErrorJournalNotActive
            && NtfsNative.TryCreateJournal(volume.Handle!, JournalMaximumSize, JournalAllocationDelta)
            && NtfsNative.TryQueryJournal(volume.Handle!, out journal, out error))
        {
            _logger.LogInformation("文件索引:{Root} 没有开启 USN 日志,已开启(同 Everything)", volume.Info.MountPath);
            return true;
        }

        Fail(volume, VolumeState.Failed, $"这个磁盘的文件变动记录不可用(错误 {error})");
        return false;
    }

    private void SaveIfDirty(IndexedVolume volume, bool force)
    {
        if (volume.Index is not { Dirty: true } index || (!force && DateTimeOffset.Now - volume.LastSave < SaveInterval))
        {
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            index.Save(Path.Combine(_directory, volume.FileName));
            volume.LastSave = DateTimeOffset.Now;
            _logger.LogInformation("文件索引:{Root} 已存盘,用时 {Elapsed} ms", volume.Info.MountPath, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "文件索引:{Root} 存盘失败", volume.Info.MountPath);
        }
    }

    private void Fail(IndexedVolume volume, VolumeState state, string message)
    {
        volume.State = state;
        volume.Message = message;
        volume.RetryAt = DateTimeOffset.Now + RetryDelay;
        _logger.LogWarning("文件索引:{Root}:{Message}", volume.Info.MountPath, message);
        Changed();
    }
}
