namespace Waio.Feature.Screenshot.Domain;

/// <summary>两帧之间的滚动:新的一帧相对上一帧,内容向上移动了 <see cref="Offset"/> 行(像素,带符号)。</summary>
/// <param name="Offset">正数 = 用户往下滚(内容向上移动);负数 = 往回滚。</param>
/// <param name="Mismatch">重叠区变化像素里对不上的比例(0 最好)。</param>
public readonly record struct ScrollMatch(int Offset, double Mismatch);

/// <summary>一次比较的结论。</summary>
public abstract record ScrollOutcome
{
    /// <summary>和上一帧一样(没滚动,或只有很小的局部变化:光标闪烁、小动画)。</summary>
    public sealed record Unchanged : ScrollOutcome;

    public sealed record Moved(ScrollMatch Match) : ScrollOutcome;

    /// <summary>有变化但对不上(滚得太多、页面自己变了、内容重复分不清)。参照帧不能前进。</summary>
    public sealed record Unknown : ScrollOutcome;
}

/// <summary>
/// 长截图的位移估计(ADR-0020):
/// <list type="bullet">
/// <item>灰度差 &gt; 10 算"变了";只在变化过的像素上算失配率;只压缩宽度、不压缩滚动方向;全范围搜索、由粗到细;
/// 失配率 &lt; 0.3 才接受;失败时参照帧不前进。</item>
/// <item>思路来自 MAIO ScrollMatcher(ADR-0014 修订三):粗层块平均到 64×128 全搜取 4 个候选,精层(宽度抽样到 ≤512)在候选附近逐像素细化;
/// 次佳位移(相差 3 行以上)必须比最佳差 0.03 以上,否则算分不清。</item>
/// </list>
/// 纯函数,线程安全。
/// </summary>
public static class ScrollMatcher
{
    /// <summary>一帧之间最多滚动画面高度的这个比例(重叠至少 30%;同 MAIO)。</summary>
    public const double MaximumShiftRatio = 0.7;

    /// <summary>像素差超过这么多才算"变了"/"对不上"(同 MAIO)。</summary>
    public const int Tolerance = 10;

    /// <summary>差异像素少于总量的 1/200(且少于 64 个)就当作没动。</summary>
    public const int StillDivisor = 200;

    /// <summary>验收:最佳位移的失配率上限。</summary>
    public const double MaximumMismatch = 0.30;

    /// <summary>次佳位移(相差 3 行以上)必须比最佳差这么多。</summary>
    public const double MinimumMargin = 0.03;

    public const int CoarseWidth = 64;
    public const int CoarseHeight = 128;
    public const int CoarseCandidates = 4;
    public const int FineWidth = 512;
    public const int FineStride = 4;

    /// <summary>粗层换算成原始坐标后可能有 1~2 像素误差,细化时向两边各多搜几行。</summary>
    public const int RefineSlack = 2;

    public static ScrollOutcome Match(GrayFrame previous, GrayFrame current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height || previous.Width <= 3 || previous.Height <= 3)
        {
            return new ScrollOutcome.Unknown();
        }

        var limit = Math.Min(previous.Height - 1, (int)(previous.Height * MaximumShiftRatio));
        if (limit < 1)
        {
            return new ScrollOutcome.Unknown();
        }

        // 匹配统一在"宽度抽样到 ≤ 512、高度不变"的精层上做(压缩宽度不影响竖直位移)
        var width = Math.Min(previous.Width, FineWidth);
        var prev = previous.Sampled(width);
        var cur = current.Sampled(width);
        var difference = new Difference(prev, cur);
        if (difference.Pixels < Math.Max(64, prev.Pixels.Length / StillDivisor))
        {
            return new ScrollOutcome.Unchanged();
        }

        var (candidates, slack) = Coarse(prev, cur, limit);
        var matches = new List<(int Offset, double Mismatch)>();
        foreach (var candidate in candidates)
        {
            var from = Math.Max(-limit, candidate - slack);
            var to = Math.Min(limit, candidate + slack);
            for (var offset = from; offset <= to; offset++)
            {
                matches.Add((offset, MismatchAt(prev, cur, difference, offset, FineStride)));
            }
        }

        return Verdict(matches, limit);
    }

    /// <summary>粗层降采样后全偏移搜索,返回换算成原始坐标的几个候选和换算误差。</summary>
    private static (List<int> Candidates, int Slack) Coarse(GrayFrame previous, GrayFrame current, int limit)
    {
        var scale = Math.Max(1, previous.Height / CoarseHeight);
        var width = Math.Min(previous.Width, CoarseWidth);
        var height = Math.Max(1, previous.Height / scale);
        var prev = previous.Reduced(width, height);
        var cur = current.Reduced(width, height);
        var coarseLimit = Math.Max(1, limit / scale);
        var difference = new Difference(prev, cur);
        var scored = new List<(int Offset, double Score)>(coarseLimit * 2);
        for (var offset = -coarseLimit; offset <= coarseLimit; offset++)
        {
            if (offset != 0)
            {
                scored.Add((offset, MismatchAt(prev, cur, difference, offset, 1)));
            }
        }

        scored.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : Math.Abs(a.Offset).CompareTo(Math.Abs(b.Offset)));

        // 块平均时行数是整除的,换算回原始坐标可能差一个粗行
        var slack = RefineSlack + previous.Height / Math.Max(1, prev.Height);
        var result = new List<int>(CoarseCandidates);
        foreach (var (offset, _) in scored)
        {
            var original = offset * scale;
            if (result.TrueForAll(r => Math.Abs(r - original) > scale * 2))
            {
                result.Add(original);
                if (result.Count == CoarseCandidates)
                {
                    break;
                }
            }
        }

        return (result, slack);
    }

    /// <summary>位移 <paramref name="offset"/> 下(当前帧第 y 行对上一帧第 y+offset 行),重叠区变化像素里对不上的比例。</summary>
    internal static double MismatchAt(GrayFrame previous, GrayFrame current, Difference difference, int offset, int stride)
    {
        var start = Math.Max(0, -offset);
        var end = Math.Min(current.Height, current.Height - offset);
        if (end - start < 2)
        {
            return 1;
        }

        var width = current.Width;
        var head = previous.Pixels;
        var tail = current.Pixels;
        var mask = difference.Mask;
        var rows = difference.Rows;
        long total = 0;
        long bad = 0;
        for (var y = start; y < end; y += stride)
        {
            // 两帧这一行都没有变化时整行跳过(固定页眉、侧栏、空白)
            if (!rows[y] && !rows[y + offset])
            {
                continue;
            }

            var here = y * width;
            var there = (y + offset) * width;
            for (var x = 0; x < width; x += stride)
            {
                if (mask[here + x] | mask[there + x])
                {
                    total++;
                    var delta = tail[here + x] - head[there + x];
                    if (delta > Tolerance || delta < -Tolerance)
                    {
                        bad++;
                    }
                }
            }
        }

        return total > 0 ? (double)bad / total : 1;
    }

    private static ScrollOutcome Verdict(List<(int Offset, double Mismatch)> matches, int limit)
    {
        if (matches.Count == 0)
        {
            return new ScrollOutcome.Unknown();
        }

        var best = matches[0];
        foreach (var m in matches)
        {
            if (m.Mismatch < best.Mismatch || (m.Mismatch == best.Mismatch && Math.Abs(m.Offset) < Math.Abs(best.Offset)))
            {
                best = m;
            }
        }

        if (best.Offset == 0)
        {
            return new ScrollOutcome.Unchanged();
        }

        if (best.Mismatch > MaximumMismatch || Math.Abs(best.Offset) >= limit)
        {
            return new ScrollOutcome.Unknown();
        }

        var second = double.MaxValue;
        foreach (var m in matches)
        {
            if (Math.Abs(m.Offset - best.Offset) > 2 && m.Mismatch < second)
            {
                second = m.Mismatch;
            }
        }

        return second != double.MaxValue && second - best.Mismatch < MinimumMargin
            ? new ScrollOutcome.Unknown()
            : new ScrollOutcome.Moved(new ScrollMatch(best.Offset, best.Mismatch));
    }

    /// <summary>
    /// 两帧同位比较:哪些像素 / 行有变化、一共有多少像素变了。只比较"有变化"的像素,固定页眉、侧栏、空白既不算证据也不会拉低匹配率
    /// (同 MAIO Difference)。
    /// </summary>
    internal sealed class Difference
    {
        public Difference(GrayFrame first, GrayFrame second)
        {
            Mask = new bool[first.Width * first.Height];
            Rows = new bool[first.Height];
            var width = first.Width;
            var a = first.Pixels;
            var b = second.Pixels;
            var changed = 0;
            for (var y = 0; y < first.Height; y++)
            {
                var row = false;
                var end = (y + 1) * width;
                for (var i = y * width; i < end; i++)
                {
                    var delta = a[i] - b[i];
                    if (delta > Tolerance || delta < -Tolerance)
                    {
                        Mask[i] = true;
                        row = true;
                        changed++;
                    }
                }

                Rows[y] = row;
            }

            Pixels = changed;
        }

        public bool[] Mask { get; }

        public bool[] Rows { get; }

        public int Pixels { get; }
    }
}
