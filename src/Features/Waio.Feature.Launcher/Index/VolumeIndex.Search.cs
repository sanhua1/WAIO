using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Index;

/// <summary>搜索时的过滤(Everything 的"排除"选项;默认全关,同 Everything)。</summary>
/// <param name="ExcludedFolders">排除的文件夹(本卷内的记录号),它们下面的都不出现。</param>
internal sealed record FileFilter(bool ExcludeHidden, bool ExcludeSystem, IReadOnlySet<int> ExcludedFolders)
{
    public static FileFilter None { get; } = new(false, false, new HashSet<int>());
}

internal readonly record struct FileHit(int Record, int Score);

/// <param name="Hits">分数从高到低的前若干条。</param>
/// <param name="Total">一共命中多少条。</param>
internal sealed record VolumeSearchResult(IReadOnlyList<FileHit> Hits, int Total);

internal sealed partial class VolumeIndex
{
    private static readonly int[] QualityScore = [0, 400, 600, 800, 1000];

    /// <summary>
    /// 搜索:最长的词在名称块上分段并行 <c>IndexOf</c>(SIMD)粗筛 → 核对命中属于哪条记录 → 检查其余的词、过滤 → 打分取前 <paramref name="limit"/> 条。
    /// 只有一个字母数字词时,再扫一遍拼音键块。打分规则同 MAIO(oil-find):匹配质量为主,用户目录、文件夹、最近修改加分,
    /// 依赖 / 系统目录、隐藏、路径深扣分。
    /// </summary>
    public VolumeSearchResult Search(FileQuery query, int limit, FileFilter filter, DateTimeOffset now, CancellationToken cancellation = default)
    {
        _lock.EnterReadLock();
        try
        {
            var nowSeconds = (uint)Math.Max(0, now.ToUnixTimeSeconds());
            var length = _names.Length;
            var segments = length < (1 << 20) ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 16);
            var paths = new PathMatcher(this, query.PathTerms);
            if (query.Primary is null)
            {
                length = Count; // 只有路径词:没有可以粗筛的名称词,按记录号逐条查(目录的结果有缓存,整遍是线性的)
                segments = length < (1 << 18) ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 16);
            }

            var partials = new (PriorityQueue<int, long> Top, int Total)[segments];
            Parallel.For(0, segments, new ParallelOptions { CancellationToken = cancellation }, s =>
            {
                var top = new PriorityQueue<int, long>(limit + 1);
                var (start, end) = ((int)((long)length * s / segments), (int)((long)length * (s + 1) / segments));
                var total = query.Primary is null
                    ? ScanRecords(query, paths, filter, nowSeconds, start, end, limit, top, cancellation)
                    : ScanSegment(query, paths, filter, nowSeconds, start, end, limit, top, cancellation);
                partials[s] = (top, total);
            });

            var merged = new PriorityQueue<int, long>(limit + 1);
            var count = 0;
            foreach (var (top, total) in partials)
            {
                count += total;
                while (top.TryDequeue(out var record, out var priority))
                {
                    Offer(merged, record, priority, limit);
                }
            }

            if (query.UsesPinyin)
            {
                count += ScanAltKeys(query, paths, filter, nowSeconds, limit, merged);
            }

            var hits = new List<FileHit>(merged.Count);
            while (merged.TryDequeue(out var record, out var priority))
            {
                hits.Add(new FileHit(record, (int)(priority >> 32)));
            }

            hits.Reverse();
            return new VolumeSearchResult(hits, count);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private int ScanSegment(
        FileQuery query, PathMatcher paths, FileFilter filter, uint now, int start, int end, int limit, PriorityQueue<int, long> top,
        CancellationToken cancellation)
    {
        var data = _names.Raw;
        var length = _names.Length;
        var term = query.Primary!;
        var total = 0;
        var position = start;
        var checks = 0;
        while (position < end)
        {
            var window = Math.Min(length, end + term.Length - 1) - position;
            if (window < term.Length)
            {
                break;
            }

            var hit = data.AsSpan(position, window).IndexOf(term);
            if (hit < 0)
            {
                break;
            }

            var at = position + hit;
            var owner = _names.OwnerAt(at, out var nameEnd);
            if (owner < 0 || owner >= Count || _name[owner] < 0 || _name[owner] > at || _name[owner] + _nameLength[owner] != nameEnd)
            {
                position = at + 1; // 命中落在记录号字节上,或者是已经作废的名称
                continue;
            }

            position = nameEnd + 5;
            var name = _names.At(_name[owner], _nameLength[owner]);
            if (_name[owner] + name.IndexOf(term) != at)
            {
                continue; // 同一个名称在上一段已经算过(一个名称里出现两次、正好跨在分段处)
            }

            if (Accepts(owner, query, paths, filter, name))
            {
                total++;
                Offer(top, owner, Priority(owner, Score(owner, query, name, now, viaAlt: false)), limit);
            }

            if ((++checks & 0xFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }
        }

        return total;
    }

    /// <summary>没有名称词时:记录号 [<paramref name="start"/>, <paramref name="end"/>) 逐条检查。</summary>
    private int ScanRecords(
        FileQuery query, PathMatcher paths, FileFilter filter, uint now, int start, int end, int limit, PriorityQueue<int, long> top,
        CancellationToken cancellation)
    {
        var total = 0;
        for (var record = start; record < end; record++)
        {
            if ((record & 0xFFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (_name[record] < 0 || (_flags[record] & (InUse | Meta)) != InUse)
            {
                continue;
            }

            var name = _names.At(_name[record], _nameLength[record]);
            if (Accepts(record, query, paths, filter, name))
            {
                total++;
                Offer(top, record, Priority(record, Score(record, query, name, now, viaAlt: false)), limit);
            }
        }

        return total;
    }

    /// <summary>拼音键块:只有含汉字的名称才有,很小,单线程扫。名称本身已经命中的不重复算。</summary>
    private int ScanAltKeys(FileQuery query, PathMatcher paths, FileFilter filter, uint now, int limit, PriorityQueue<int, long> top)
    {
        var term = query.Primary!;
        var seen = new HashSet<int>();
        var position = 0;
        var total = 0;
        while (position < _alt.Length)
        {
            var hit = _alt.Span[position..].IndexOf(term);
            if (hit < 0)
            {
                break;
            }

            var at = position + hit;
            var owner = _alt.OwnerAt(at, out var keyEnd);
            position = keyEnd < 0 ? at + 1 : keyEnd + 5;
            if (owner < 0 || owner >= Count || !_altOf.ContainsKey(owner) || !seen.Add(owner) || _name[owner] < 0)
            {
                continue;
            }

            var name = _names.At(_name[owner], _nameLength[owner]);
            if (name.IndexOf(term) >= 0 || !Accepts(owner, query, paths, filter, name))
            {
                continue;
            }

            total++;
            Offer(top, owner, Priority(owner, Score(owner, query, name, now, viaAlt: true)), limit);
        }

        return total;
    }

    /// <summary>其余的词、路径词、文件夹 / 隐藏 / 系统 / 排除目录的过滤。</summary>
    private bool Accepts(int record, FileQuery query, PathMatcher paths, FileFilter filter, ReadOnlySpan<byte> name)
    {
        var flags = _flags[record];
        if ((flags & (InUse | Meta)) != InUse || record == RootRecord
            || (query.FoldersOnly && (flags & Directory) == 0)
            || (filter.ExcludeHidden && (flags & Hidden) != 0)
            || (filter.ExcludeSystem && (flags & SystemFile) != 0))
        {
            return false;
        }

        foreach (var term in query.Terms)
        {
            if (!ReferenceEquals(term, query.Primary) && name.IndexOf(term) < 0 && !AltContains(record, term))
            {
                return false;
            }
        }

        if (!paths.Matches(record))
        {
            return false;
        }

        if (filter.ExcludedFolders.Count > 0)
        {
            var current = record;
            for (var guard = 0; guard < 256 && current != RootRecord && current >= 0 && current < Count; guard++)
            {
                if (filter.ExcludedFolders.Contains(current))
                {
                    return false;
                }

                current = _parent[current];
            }
        }

        // Everything 的"排除隐藏 / 系统"也作用于它们下面的内容;这里只看条目自己,
        // 隐藏目录里的东西照样能搜到(选项默认关闭,和 Everything 一致,所以默认行为相同)
        return true;
    }

    private bool AltContains(int record, ReadOnlySpan<byte> term)
    {
        if (!_altOf.TryGetValue(record, out var start))
        {
            return false;
        }

        for (var key = 0; key < 2 && start < _alt.Length; key++)
        {
            if (_alt.OwnerAt(start, out var end) != record)
            {
                break;
            }

            if (_alt.At(start, end - start).IndexOf(term) >= 0)
            {
                return true;
            }

            start = end + 5;
        }

        return false;
    }

    private int Score(int record, FileQuery query, ReadOnlySpan<byte> name, uint now, bool viaAlt)
    {
        // 只有路径词时名称没有可比的,一律按"词的开头"算,靠深度 / 修改时间等排序(浅的在前)
        var quality = query.Primary is null ? 2 : NameMatch.Quality(name, query.Primary);
        if (viaAlt && quality == 0)
        {
            quality = 2; // 拼音命中算"词的开头"(同 MAIO)
        }

        var penalty = query.Primary is null ? 0 : 2 * Math.Min(64, Math.Max(0, name.Length - query.Primary.Length));
        var score = QualityScore[quality] - penalty;
        var flags = _flags[record];
        if ((flags & InHome) != 0)
        {
            score += 60;
        }

        if ((flags & Directory) != 0)
        {
            score += 20;
        }

        var age = now > _mtime[record] ? now - _mtime[record] : 0;
        score += age switch
        {
            <= 86_400 => 60,
            <= 604_800 => 45,
            <= 2_592_000 => 30,
            <= 31_536_000 => 10,
            _ => 0,
        };
        if ((flags & Clutter) != 0)
        {
            score -= 350;
        }

        if ((flags & Hidden) != 0)
        {
            score -= 200;
        }

        return score - (5 * Math.Min(16, (int)_depth[record]));
    }

    /// <summary>堆的优先级:分数在高 32 位;同分时记录号小的(通常更早建、路径更浅)优先。</summary>
    private static long Priority(int record, int score) => ((long)score << 32) | (uint)(int.MaxValue - record);

    /// <summary>小顶堆只留分数最高的 <paramref name="limit"/> 条。</summary>
    private static void Offer(PriorityQueue<int, long> top, int record, long priority, int limit)
    {
        if (top.Count < limit)
        {
            top.Enqueue(record, priority);
        }
        else if (top.TryPeek(out _, out var lowest) && priority > lowest)
        {
            top.DequeueEnqueue(record, priority);
        }
    }
}
