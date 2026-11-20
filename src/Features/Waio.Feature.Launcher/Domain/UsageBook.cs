namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 常用度:从启动台打开过的条目和当时的输入(规则同 MAIO 的 UsageBook)。
/// <list type="bullet">
/// <item>每个条目一个分数,每打开一次加 1,按时间衰减(一周减半);</item>
/// <item>记下"输入了什么、选了哪一项":以后同样的输入,这一项排第一。</item>
/// </list>
/// 存成 data/launcher/usage.json(<see cref="Infrastructure.UsageStore"/>)。只在 UI 线程上修改。
/// </summary>
internal sealed class UsageBook
{
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(7);
    public const int MaxQueries = 400;
    public const int MaxItems = 1000;

    public Dictionary<string, UsageEntry> Items { get; set; } = [];

    /// <summary>输入(统一形式)→ 条目 id → 次数。</summary>
    public Dictionary<string, Dictionary<string, int>> Queries { get; set; } = [];

    public void Record(string id, string query, DateTimeOffset now)
    {
        Items[id] = new UsageEntry { Score = Usage(id, now) + 1, Updated = now };
        var key = SearchKeys.Normalize(query);
        if (key.Length > 0)
        {
            if (!Queries.TryGetValue(key, out var picks))
            {
                Queries[key] = picks = [];
            }

            picks[id] = picks.GetValueOrDefault(id) + 1;
        }

        Prune(now);
    }

    /// <summary>衰减到 <paramref name="now"/> 的分数。</summary>
    public double Usage(string id, DateTimeOffset now)
    {
        if (!Items.TryGetValue(id, out var entry))
        {
            return 0;
        }

        var elapsed = Math.Max(0, (now - entry.Updated).TotalSeconds);
        return entry.Score * Math.Pow(0.5, elapsed / HalfLife.TotalSeconds);
    }

    /// <summary>
    /// 排序加分(和 <see cref="MatchScore"/> 的档次同一个量级):常用度最多 +30;
    /// 这次的输入以前选过它 +25~40;以前输入过以它开头的内容并选了它 +10。
    /// </summary>
    public int Bonus(string id, string query, DateTimeOffset now)
    {
        var bonus = (int)Math.Min(30, 10 * Math.Log2(1 + Usage(id, now)));
        var key = SearchKeys.Normalize(query);
        if (key.Length == 0)
        {
            return bonus;
        }

        if (Queries.TryGetValue(key, out var picks) && picks.TryGetValue(id, out var count))
        {
            return bonus + 20 + Math.Min(20, count * 5);
        }

        foreach (var (text, chosen) in Queries)
        {
            if (text.StartsWith(key, StringComparison.Ordinal) && chosen.ContainsKey(id))
            {
                return bonus + 10;
            }
        }

        return bonus;
    }

    /// <summary>最近常用的条目 id,按衰减后的分数从高到低。</summary>
    public IReadOnlyList<string> Recent(int limit, DateTimeOffset now) =>
        Items.Keys.OrderByDescending(id => Usage(id, now)).ThenBy(id => id, StringComparer.Ordinal).Take(limit).ToList();

    public void Forget(string id)
    {
        Items.Remove(id);
        foreach (var key in Queries.Keys.ToList())
        {
            var picks = Queries[key];
            picks.Remove(id);
            if (picks.Count == 0)
            {
                Queries.Remove(key);
            }
        }
    }

    /// <summary>控制大小:分数最低的条目、次数最少的输入记录删掉。</summary>
    private void Prune(DateTimeOffset now)
    {
        if (Items.Count > MaxItems)
        {
            var keep = Recent(MaxItems, now).ToHashSet(StringComparer.Ordinal);
            foreach (var id in Items.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                Items.Remove(id);
            }
        }

        if (Queries.Count > MaxQueries)
        {
            var keep = Queries.OrderByDescending(q => q.Value.Values.Sum()).Take(MaxQueries).Select(q => q.Key).ToHashSet();
            foreach (var key in Queries.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                Queries.Remove(key);
            }
        }
    }
}

internal sealed class UsageEntry
{
    public double Score { get; set; }

    public DateTimeOffset Updated { get; set; }
}
