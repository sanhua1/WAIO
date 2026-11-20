namespace Waio.Feature.Clipboard.Domain;

/// <summary>存储上限选项(参考 MAIO)。</summary>
public enum StorageLimit
{
    Mb500,
    Gb1,
    Gb2,
    Gb5,
    Gb10,
    Gb20,

    /// <summary>永久保留(不按大小清理)。</summary>
    Unlimited,
}

internal static class StorageLimits
{
    /// <summary>字节数;<c>null</c> = 不限制。用十进制 MB/GB,和磁盘厂商、MAIO 一致。</summary>
    public static long? Bytes(this StorageLimit limit) => limit switch
    {
        StorageLimit.Mb500 => 500_000_000,
        StorageLimit.Gb1 => 1_000_000_000,
        StorageLimit.Gb2 => 2_000_000_000,
        StorageLimit.Gb5 => 5_000_000_000,
        StorageLimit.Gb10 => 10_000_000_000,
        StorageLimit.Gb20 => 20_000_000_000,
        _ => null,
    };
}

/// <summary>清理规则(纯函数)。<b>固定的条目永远不会被清理。</b></summary>
internal static class RetentionPolicy
{
    internal sealed record Entry(long Id, long ByteSize, DateTimeOffset LastUsedAt, bool IsPinned);

    /// <summary>总大小(含固定的)超过上限时,从最久没用过的未固定条目开始删,直到不超过上限。<paramref name="limit"/> 为 null 表示不限制。</summary>
    public static IReadOnlyList<long> OverLimit(IReadOnlyCollection<Entry> entries, long? limit)
    {
        if (limit is not { } max)
        {
            return [];
        }

        var total = entries.Sum(e => e.ByteSize);
        var removed = new List<long>();
        foreach (var entry in OldestFirst(entries))
        {
            if (total <= max)
            {
                break;
            }

            removed.Add(entry.Id);
            total -= entry.ByteSize;
        }

        return removed;
    }

    /// <summary>删除最旧的、合计至少 <paramref name="bytes"/> 字节的未固定条目。</summary>
    public static IReadOnlyList<long> Oldest(IReadOnlyCollection<Entry> entries, long bytes)
    {
        long freed = 0;
        var removed = new List<long>();
        foreach (var entry in OldestFirst(entries))
        {
            if (freed >= bytes)
            {
                break;
            }

            removed.Add(entry.Id);
            freed += entry.ByteSize;
        }

        return removed;
    }

    /// <summary>删除最后使用时间早于 <paramref name="date"/> 的未固定条目。</summary>
    public static IReadOnlyList<long> Before(IReadOnlyCollection<Entry> entries, DateTimeOffset date) =>
        [.. entries.Where(e => !e.IsPinned && e.LastUsedAt < date).Select(e => e.Id)];

    private static IEnumerable<Entry> OldestFirst(IEnumerable<Entry> entries) =>
        entries.Where(e => !e.IsPinned).OrderBy(e => e.LastUsedAt);
}
