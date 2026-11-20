using Microsoft.Extensions.Logging;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Infrastructure.Ntfs;

namespace Waio.Feature.Launcher.Index;

internal enum VolumeState
{
    Waiting,
    Loading,
    Scanning,
    Ready,
    NeedsAdmin,
    Unsupported,
    Failed,
}

/// <summary>一个卷的索引状态(设置页、面板上显示)。</summary>
internal sealed record VolumeStatus(
    string Root, string FileSystem, VolumeState State, int Entries, long MemoryBytes, DateTimeOffset? ScannedAt,
    long RecordsRead, long RecordsTotal, string? Message);

/// <summary>文件搜索的结果:前若干条 + 一共多少条。</summary>
internal sealed record FileSearchResults(IReadOnlyList<FileHitInfo> Hits, int Total);

/// <summary>
/// 全盘文件索引(ADR-0024,做法同 Everything:读 NTFS 的 $MFT 建库,USN 日志实时更新,存盘后下次启动直接读回来接着读日志)。
/// <para>
/// 一个专用后台线程做全部的写:列卷(每 60 秒看一次,新的固定 NTFS 卷自动加进来,拔掉的去掉——Everything 的
/// auto_include_fixed_volumes / auto_remove_offline_ntfs_volumes)、读盘或重建、每秒读一次日志(Everything 的
/// monitor_update_delay 1000 ms)、有变化时每 30 分钟存盘。搜索在任意线程调用(各卷的读锁)。
/// </para>
/// 本文件:生命周期、搜索、状态;<c>FileIndex.Volumes.cs</c>:单个卷的读盘 / 重建 / 读日志。
/// </summary>
internal sealed partial class FileIndex : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan VolumeRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly string _directory;
    private readonly string? _home;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<IndexedVolume> _volumes = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private LauncherSettings _settings;
    private bool _rebuildRequested;
    private bool _settingsChanged;

    public FileIndex(string dataDirectory, string? home, LauncherSettings settings, ILogger logger)
    {
        _directory = Path.Combine(dataDirectory, "index");
        _home = home;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>状态变了(进度、条目数、出错)。在后台线程上触发。</summary>
    public event Action? StatusChanged;

    public void Start()
    {
        Directory.CreateDirectory(_directory);
        _thread = new Thread(Run) { IsBackground = true, Name = "launcher-index", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>停止并把有变化的卷存盘(功能停用、WAIO 退出时)。</summary>
    public void Stop(TimeSpan timeout)
    {
        _stop.Cancel();
        _wake.Set();
        if (_thread is { } thread && !thread.Join(timeout))
        {
            _logger.LogWarning("文件索引线程没有及时停下");
            return;
        }

        foreach (var volume in Snapshot())
        {
            SaveIfDirty(volume, force: true);
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(5));
        foreach (var volume in Snapshot())
        {
            volume.Handle?.Dispose();
        }

        _wake.Dispose();
        _stop.Dispose();
    }

    /// <summary>设置变了:可移动卷、排除选项在后台线程上重新应用。</summary>
    public void ApplySettings(LauncherSettings settings)
    {
        _settings = settings;
        _settingsChanged = true;
        _wake.Set();
    }

    /// <summary>扔掉现有索引,全部重新扫描(设置页的"重建索引")。</summary>
    public void Rebuild()
    {
        _rebuildRequested = true;
        _wake.Set();
    }

    public IReadOnlyList<VolumeStatus> Status() => Snapshot().Select(v => v.Status()).ToList();

    /// <summary>所有就绪的卷一起搜,按分数合并取前 <paramref name="limit"/> 条。</summary>
    public FileSearchResults Search(FileQuery query, int limit, CancellationToken cancellation = default)
    {
        var hits = new List<(IndexedVolume Volume, FileHit Hit)>();
        var total = 0;
        foreach (var volume in Snapshot())
        {
            if (volume.Index is not { } index)
            {
                continue;
            }

            cancellation.ThrowIfCancellationRequested();
            var result = index.Search(query, limit, volume.Filter, DateTimeOffset.Now, cancellation);
            total += result.Total;
            hits.AddRange(result.Hits.Select(h => (volume, h)));
        }

        var described = hits
            .OrderByDescending(h => h.Hit.Score)
            .Take(limit)
            .Select(h => h.Volume.Index?.Describe(h.Hit.Record))
            .OfType<FileHitInfo>()
            .ToList();
        return new FileSearchResults(described, total);
    }

    private List<IndexedVolume> Snapshot()
    {
        lock (_gate)
        {
            return [.. _volumes];
        }
    }

    private void Run()
    {
        var cancellation = _stop.Token;
        var lastRefresh = DateTimeOffset.MinValue;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (_rebuildRequested)
                {
                    _rebuildRequested = false;
                    foreach (var volume in Snapshot())
                    {
                        volume.Discard(Path.Combine(_directory, volume.FileName));
                    }

                    lastRefresh = DateTimeOffset.MinValue;
                }

                if (_settingsChanged || DateTimeOffset.Now - lastRefresh > VolumeRefreshInterval)
                {
                    var settingsChanged = _settingsChanged;
                    _settingsChanged = false;
                    RefreshVolumes();
                    if (settingsChanged)
                    {
                        foreach (var volume in Snapshot())
                        {
                            volume.Filter = FilterFor(volume);
                        }
                    }

                    lastRefresh = DateTimeOffset.Now;
                }

                foreach (var volume in Snapshot())
                {
                    cancellation.ThrowIfCancellationRequested();
                    Tick(volume, cancellation);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文件索引线程出错(继续运行)");
            }

            _wake.WaitOne(PollInterval);
        }
    }

    /// <summary>按设置算出要索引哪些卷(Everything 的默认:所有固定的 NTFS 卷;可移动卷要打开设置)。</summary>
    private void RefreshVolumes()
    {
        var wanted = Volumes.List()
            .Where(v => v.Kind == VolumeKind.Fixed || (v.Kind == VolumeKind.Removable && _settings.IncludeRemovableVolumes))
            .Where(v => v.IsNtfs || v.IsRefs)
            .ToList();
        lock (_gate)
        {
            foreach (var gone in _volumes.Where(v => !wanted.Exists(w => w.GuidPath == v.Info.GuidPath)).ToList())
            {
                _logger.LogInformation("文件索引:卷 {Root} 不在了,去掉", gone.Info.MountPath);
                gone.Handle?.Dispose();
                _volumes.Remove(gone);
            }

            foreach (var info in wanted.Where(w => !_volumes.Exists(v => v.Info.GuidPath == w.GuidPath)))
            {
                _volumes.Add(new IndexedVolume(info));
            }

            _volumes.Sort((a, b) => string.Compare(a.Info.MountPath, b.Info.MountPath, StringComparison.OrdinalIgnoreCase));
        }

        Changed();
    }

    /// <summary>设置里的排除项 → 这个卷上的过滤条件(排除的文件夹解析成记录号)。</summary>
    private FileFilter FilterFor(IndexedVolume volume)
    {
        var folders = new HashSet<int>();
        if (volume.Index is { } index)
        {
            foreach (var path in _settings.ExcludeFolders)
            {
                if (index.FindFolder(path) is var record and >= 0)
                {
                    folders.Add(record);
                }
            }
        }

        return new FileFilter(_settings.ExcludeHiddenFiles, _settings.ExcludeSystemFiles, folders);
    }

    private void Changed()
    {
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "文件索引状态通知失败");
        }
    }
}
