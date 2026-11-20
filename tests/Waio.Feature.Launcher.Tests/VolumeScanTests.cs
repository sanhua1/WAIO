using System.Diagnostics;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Index;
using Waio.Feature.Launcher.Infrastructure.Ntfs;
using Xunit.Abstractions;

namespace Waio.Feature.Launcher.Tests;

/// <summary>只在设置环境变量 WAIO_VOLUME_READ=1 时运行:要管理员权限,只读地读系统盘的 $MFT 和 USN 日志(不写盘、不开窗口、不发按键)。</summary>
public sealed class VolumeReadFactAttribute : FactAttribute
{
    public VolumeReadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WAIO_VOLUME_READ") != "1")
        {
            Skip = "设置环境变量 WAIO_VOLUME_READ=1 才运行(要管理员权限,只读系统盘的 MFT)";
        }
    }
}

public class VolumeScanTests(ITestOutputHelper output)
{
    [VolumeReadFact]
    public void 系统盘_读MFT建索引_搜索_存盘读盘()
    {
        var volume = Volumes.List().First(v => v.MountPath.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase));
        using var handle = NtfsNative.OpenVolume(volume.DevicePath, out var error);
        Assert.False(handle.IsInvalid, $"打开卷失败 {error}(要管理员)");
        Assert.True(NtfsNative.TryGetVolumeData(handle, out var data));
        Assert.True(NtfsNative.TryQueryJournal(handle, out var journal, out _));

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        var reader = new MftReader(handle, data);
        var index = new VolumeIndex(volume.MountPath, volume.Serial, (int)reader.RecordCount, Environment.GetEnvironmentVariable("USERPROFILE"));
        var builder = new MftIndexBuilder(index);
        reader.ReadAll(builder.Visit);
        var read = watch.Elapsed;
        index.Derive();
        var built = watch.Elapsed;
        var after = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"{volume.MountPath} MFT 记录 {reader.RecordCount:N0},在用 {builder.Records:N0},可见条目 {index.Entries:N0}");
        output.WriteLine($"读取+解析 {read.TotalSeconds:F2} s,派生 {(built - read).TotalSeconds:F2} s;托管内存增加 {(after - before) / 1048576.0:F0} MB,估算 {index.MemoryBytes / 1048576.0:F0} MB");

        foreach (var text in new[] { "notepad", "a", "windows", "微信", "readme md" })
        {
            var query = FileQuery.Parse(text, foldersOnly: false)!;
            watch.Restart();
            var result = index.Search(query, 20, FileFilter.None, DateTimeOffset.Now);
            output.WriteLine($"搜 \"{text}\":{result.Total:N0} 条,{watch.Elapsed.TotalMilliseconds:F0} ms,第一条 {(result.Hits.Count > 0 ? index.PathOf(result.Hits[0].Record) : "-")}");
        }

        Assert.True(index.Entries > 1000);
        Assert.Contains(@"\Windows\System32", index.PathOf(index.FindFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"))));

        var file = Path.Combine(Path.GetTempPath(), $"waio-index-test-{Guid.NewGuid():N}.bin");
        try
        {
            index.JournalId = journal.UsnJournalId;
            index.NextUsn = journal.NextUsn;
            watch.Restart();
            index.Save(file);
            var saved = watch.Elapsed;
            watch.Restart();
            var loaded = VolumeIndex.Load(file, volume.MountPath, volume.Serial, null);
            output.WriteLine($"存盘 {saved.TotalSeconds:F2} s({new FileInfo(file).Length / 1048576.0:F0} MB),读盘 {watch.Elapsed.TotalSeconds:F2} s");
            Assert.NotNull(loaded);
            Assert.Equal(index.Entries, loaded.Entries);
            var query = FileQuery.Parse("notepad", false)!;
            Assert.Equal(index.Search(query, 5, FileFilter.None, DateTimeOffset.Now).Total, loaded.Search(query, 5, FileFilter.None, DateTimeOffset.Now).Total);
        }
        finally
        {
            File.Delete(file);
        }

        // 日志:从扫描前的位置读一批,能解析
        var buffer = new byte[1 << 16];
        var bytes = NtfsNative.ReadJournal(handle, journal.UsnJournalId, journal.NextUsn, buffer, out _);
        Assert.True(bytes >= 8);
        var changes = new List<UsnChange>();
        UsnRecords.Parse(buffer.AsSpan(0, bytes), changes);
        output.WriteLine($"扫描期间的日志变化 {changes.Count} 条");
    }
}

public class VolumeJournalTests(ITestOutputHelper output)
{
    /// <summary>在临时目录建一个文件、改名、删除,看 USN 日志能不能让索引跟上(只动自己建的临时文件)。</summary>
    [VolumeReadFact]
    public void 系统盘_新建改名删除_读日志后能搜到()
    {
        var temp = Path.GetTempPath();
        var volume = Volumes.List().First(v => temp.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase));
        using var handle = NtfsNative.OpenVolume(volume.DevicePath, out _);
        NtfsNative.TryGetVolumeData(handle, out var data);
        NtfsNative.TryQueryJournal(handle, out var journal, out _);
        var reader = new MftReader(handle, data);
        var index = new VolumeIndex(volume.MountPath, volume.Serial, (int)reader.RecordCount, null)
        {
            JournalId = journal.UsnJournalId, NextUsn = journal.NextUsn,
        };
        reader.ReadAll(new MftIndexBuilder(index).Visit);
        index.Derive();

        var name = $"waio测试{Guid.NewGuid():N}"[..20];
        var file = Path.Combine(temp, name + ".txt");
        File.WriteAllText(file, new string('x', 5000));
        Pump(index, handle);
        var hits = index.Search(FileQuery.Parse(name, false)!, 5, FileFilter.None, DateTimeOffset.Now).Hits;
        Assert.Single(hits);
        Assert.Equal(file, index.PathOf(hits[0].Record), ignoreCase: true);
        Assert.Equal(5000, index.SizeOf(hits[0].Record));

        var renamed = Path.Combine(temp, name + "-改名.md");
        File.Move(file, renamed);
        Pump(index, handle);
        Assert.Equal(renamed, index.PathOf(index.Search(FileQuery.Parse(name, false)!, 5, FileFilter.None, DateTimeOffset.Now).Hits.Single().Record), ignoreCase: true);

        File.Delete(renamed);
        Pump(index, handle);
        Assert.Empty(index.Search(FileQuery.Parse(name, false)!, 5, FileFilter.None, DateTimeOffset.Now).Hits);
        output.WriteLine("新建 / 改名 / 删除都跟上了");
    }

    private static void Pump(VolumeIndex index, Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new byte[1 << 20];
        while (true)
        {
            var bytes = NtfsNative.ReadJournal(handle, index.JournalId, index.NextUsn, buffer, out var error);
            Assert.True(bytes >= 8, $"读日志失败 {error}");
            var changes = new List<UsnChange>();
            var next = UsnRecords.Parse(buffer.AsSpan(0, bytes), changes);
            index.Apply(changes, r => NtfsNative.TryGetInfoById(handle, r, out var s, out var w, out var a) ? new FileDetails(s, w, a) : null);
            index.NextUsn = next;
            if (bytes < buffer.Length / 2)
            {
                return;
            }
        }
    }
}
