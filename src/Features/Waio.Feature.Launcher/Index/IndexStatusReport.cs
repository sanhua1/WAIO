using System.Text.Json;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Index;

/// <summary>
/// 索引状态的两种呈现:面板标签栏右边的一句话,和给设置页读的 data/launcher/index-status.json
/// (设置页和运行中的功能之间只隔着这个文件,不需要新的 Kit 契约)。
/// </summary>
internal static class IndexStatusReport
{
    public const string FileName = "index-status.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>面板上的一句话;全部就绪时返回 null。</summary>
    public static string? Summary(IReadOnlyList<VolumeStatus> volumes)
    {
        if (volumes.Count == 0)
        {
            return "没有可以索引的磁盘(只支持 NTFS 格式)";
        }

        if (volumes.FirstOrDefault(v => v.State == VolumeState.Scanning) is { } scanning)
        {
            var percent = scanning.RecordsTotal > 0 ? (int)(100 * scanning.RecordsRead / scanning.RecordsTotal) : 0;
            return $"正在建立文件索引:{scanning.Root.TrimEnd('\\')} {percent}%";
        }

        if (volumes.Any(v => v.State is VolumeState.Loading or VolumeState.Waiting))
        {
            return "正在读取文件索引…";
        }

        if (volumes.All(v => v.State == VolumeState.NeedsAdmin))
        {
            return "文件搜索需要管理员权限";
        }

        return volumes.Any(v => v.State == VolumeState.Ready) ? null : "文件索引不可用,详见设置";
    }

    public static void Write(string dataDirectory, IReadOnlyList<VolumeStatus> volumes)
    {
        var report = new IndexReport(DateTimeOffset.Now, volumes.Select(v => new VolumeReport(
            v.Root, v.FileSystem, v.State.ToString(), v.Entries, v.MemoryBytes, v.ScannedAt,
            v.RecordsTotal > 0 ? (int)(100 * v.RecordsRead / v.RecordsTotal) : 0, v.Message)).ToList());
        var path = Path.Combine(dataDirectory, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static IndexReport? Read(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<IndexReport>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed record IndexReport(DateTimeOffset UpdatedAt, List<VolumeReport> Volumes);

internal sealed record VolumeReport(
    string Root, string FileSystem, string State, int Entries, long MemoryBytes, DateTimeOffset? ScannedAt, int Percent, string? Message);

/// <summary>把 <see cref="FileIndex"/> 接到面板上的文件搜索接口(搜索结果 → 结果条目)。</summary>
internal sealed class IndexFileSearch(Func<FileIndex?> index) : Panel.IFileSearch
{
    public FileResults Search(string text, bool foldersOnly, int limit, CancellationToken cancellation)
    {
        if (index() is not { } current || FileQuery.Parse(text, foldersOnly) is not { } query)
        {
            return new FileResults(text, [], 0);
        }

        var found = current.Search(query, limit, cancellation);
        return new FileResults(text, found.Hits.Select(h => LauncherItem.File(h.Path, h.Name, h.IsDirectory)).ToList(), found.Total);
    }
}
