namespace Waio.Feature.Screenshot.Domain;

/// <summary>一条截图历史(MAIO ScreenshotRecord,ADR-0012)。图片文件放在 <c>&lt;历史目录&gt;\&lt;id&gt;\</c>。</summary>
public sealed record ScreenshotRecord
{
    public required string Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    /// <summary>截图时最前面的程序(进程名,不含 .exe)。</summary>
    public string? SourceApp { get; init; }

    /// <summary>识别出的文字(P4),用于搜索。</summary>
    public string? OcrText { get; init; }

    /// <summary>这条记录占用的字节数(全部文件)。</summary>
    public long Bytes { get; init; }

    /// <summary>收藏:不会被自动清理。</summary>
    public bool Pinned { get; init; }

    /// <summary>有标注时才有 annotations.json 和 rendered.png;没有标注时最终图就是 original.png。</summary>
    public bool HasAnnotations { get; init; }

    /// <summary>截图时的缩放比例(每 pt 的像素数;重新编辑标注时用)。</summary>
    public double? Scale { get; init; }
}

/// <summary><c>index.json</c> 的内容。</summary>
public sealed record ScreenshotIndex
{
    public int Version { get; init; } = 1;

    /// <summary>按时间倒序(最新的在前)。</summary>
    public List<ScreenshotRecord> Records { get; init; } = [];
}

/// <summary>保存截图时的文件名(MAIO ScreenshotFileName):<c>WAIO 2026-11-03 14.05.33.png</c>,重名时加序号。</summary>
public static class ScreenshotFileName
{
    public static string Make(DateTimeOffset localTime, Func<string, bool> exists, string extension = "png")
    {
        var stem = $"WAIO {localTime:yyyy-MM-dd HH.mm.ss}";
        var name = $"{stem}.{extension}";
        for (var index = 2; exists(name); index++)
        {
            name = $"{stem} {index}.{extension}";
        }

        return name;
    }
}

/// <summary>截图历史的存储上限(MAIO HistoryLimit,用户选择默认 2 GB)。</summary>
public enum HistoryLimit
{
    Gb1,
    Gb2,
    Gb5,
    Gb10,
    Gb20,
    Unlimited,
}

public static class HistoryLimits
{
    public static long? Bytes(this HistoryLimit limit) => limit switch
    {
        HistoryLimit.Gb1 => 1_000_000_000,
        HistoryLimit.Gb2 => 2_000_000_000,
        HistoryLimit.Gb5 => 5_000_000_000,
        HistoryLimit.Gb10 => 10_000_000_000,
        HistoryLimit.Gb20 => 20_000_000_000,
        _ => null,
    };

    public static string Title(this HistoryLimit limit) => limit switch
    {
        HistoryLimit.Unlimited => "不限制(永久保留)",
        _ => $"{limit.Bytes()!.Value / 1_000_000_000} GB",
    };
}

/// <summary>截图历史的清理规则(MAIO HistoryRetention,和剪贴板的规则一致、各自实现)。收藏的记录永远不清理。</summary>
public static class HistoryRetention
{
    /// <summary>总大小超过上限时,从最旧的开始删,直到不超过上限。<paramref name="limit"/> 为 null 表示不限制。</summary>
    public static IReadOnlyList<string> OverLimit(IReadOnlyList<ScreenshotRecord> records, long? limit)
    {
        if (limit is not { } max)
        {
            return [];
        }

        var total = records.Sum(r => r.Bytes);
        var removed = new List<string>();
        foreach (var record in OldestFirst(records))
        {
            if (total <= max)
            {
                break;
            }

            removed.Add(record.Id);
            total -= record.Bytes;
        }

        return removed;
    }

    /// <summary>删除最旧的、合计至少 <paramref name="bytes"/> 字节的记录。</summary>
    public static IReadOnlyList<string> Oldest(IReadOnlyList<ScreenshotRecord> records, long bytes)
    {
        long freed = 0;
        var removed = new List<string>();
        foreach (var record in OldestFirst(records))
        {
            if (freed >= bytes)
            {
                break;
            }

            removed.Add(record.Id);
            freed += record.Bytes;
        }

        return removed;
    }

    public static IReadOnlyList<string> Before(IReadOnlyList<ScreenshotRecord> records, DateTimeOffset date) =>
        [.. records.Where(r => !r.Pinned && r.CreatedAt < date).Select(r => r.Id)];

    private static IEnumerable<ScreenshotRecord> OldestFirst(IEnumerable<ScreenshotRecord> records) =>
        records.Where(r => !r.Pinned).OrderBy(r => r.CreatedAt);
}
