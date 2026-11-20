namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 长截图的拼接计划(MAIO StitchPlan,ADR-0014 修订三;ADR-0020)。
/// 显式维护"当前画面顶部在画布中的位置",按<b>绝对位置</b>写入新帧露出的部分,而不是"往画布末尾追加":
/// <list type="bullet">
/// <item>正常往下滚只写新帧靠下的一段(重叠部分被覆盖,不会重复):
/// 吸顶栏永远在上半帧,不会被写进画布中间;页脚每帧都被下一帧的真实内容盖掉,最后只在末尾出现一次;</item>
/// <item>往回滚时只更新位置、不写图;滚过起点才真正向画布头部扩展;</item>
/// <item>匹配成功就更新位置,所以往回滚之后能立刻接着拼。</item>
/// </list>
/// </summary>
public sealed class StitchPlan(int frameHeight)
{
    /// <summary>画布上的一段:第 <paramref name="Frame"/> 帧的 [<paramref name="Start"/>, <paramref name="End"/>) 行,放在画布第 <paramref name="Top"/> 行。</summary>
    public readonly record struct Strip(int Frame, int Start, int End, int Top)
    {
        public int Height => End - Start;
    }

    private List<Strip> _strips = [];

    public int FrameHeight { get; } = frameHeight;

    public IReadOnlyList<Strip> Strips => _strips;

    public int CanvasHeight { get; private set; }

    /// <summary>最新一帧顶部在画布中的位置(画布已经向下平移过,所以不会是负数)。</summary>
    public int Position { get; private set; }

    /// <summary>还在用的帧。</summary>
    public IReadOnlySet<int> UsedFrames => _strips.Select(s => s.Frame).ToHashSet();

    /// <summary>第一帧:铺满画布。</summary>
    public void Begin(int frame = 0)
    {
        _strips = [new Strip(frame, 0, FrameHeight, 0)];
        CanvasHeight = FrameHeight;
        Position = 0;
    }

    /// <summary>
    /// 接上滚动了 <paramref name="offset"/> 行的新一帧(正数 = 往下滚)。
    /// 返回需要写进画布的帧内行 [start, end);新帧完全落在已拼范围里时返回 null(画布不动,只更新位置)。
    /// </summary>
    public (int Start, int End)? Append(int frame, int offset)
    {
        var rawTop = Position + offset;
        if (rawTop < 0)
        {
            // 往回滚超过了画布开头:画布整体下移,新露出的头部由这一帧的上半帧补上
            var exposed = -rawTop;
            Shift(exposed);
            var end = Math.Min(FrameHeight, Math.Max(exposed, FrameHeight / 2));
            return Write(0, end, frame, 0);
        }

        var bottom = rawTop + FrameHeight;
        if (bottom <= CanvasHeight)
        {
            Position = rawTop;
            return null;
        }

        // 往下滚:只写新帧靠下的一段。band 随位移变大而变大,保证:
        // - 至少覆盖新露出的内容(不会留缝);
        // - 往前多覆盖最多半帧,把上一帧临时写下的页脚盖掉;
        // - 顶部留出至少 1/4 帧,正常的吸顶栏不会被写进画布中间。
        var band = Math.Min(FrameHeight, Math.Max(FrameHeight / 2, FrameHeight / 4 + offset));
        var start = FrameHeight - band;
        return Write(rawTop + start, bottom, frame, rawTop);
    }

    /// <summary>把画布 [from, to) 换成第 <paramref name="frame"/> 帧(顶部在画布 <paramref name="top"/>)的对应行,返回帧内行。</summary>
    private (int Start, int End)? Write(int from, int to, int frame, int top)
    {
        from = Math.Max(from, top);
        to = Math.Min(to, top + FrameHeight);
        if (from >= to)
        {
            Position = Math.Max(0, top);
            return null;
        }

        Overwrite(from, to, frame, top);
        CanvasHeight = Math.Max(CanvasHeight, top + FrameHeight);
        Position = Math.Max(0, top);
        return (from - top, to - top);
    }

    /// <summary>用新段覆盖画布上的 [from, to),断开的旧段裁剪后保留。</summary>
    private void Overwrite(int from, int to, int frame, int top)
    {
        var kept = new List<Strip>(_strips.Count + 1);
        foreach (var strip in _strips)
        {
            var lower = strip.Top;
            var upper = strip.Top + strip.Height;
            if (upper <= from || lower >= to)
            {
                kept.Add(strip);
                continue;
            }

            if (lower < from)
            {
                kept.Add(strip with { End = strip.Start + (from - lower) });
            }

            if (upper > to)
            {
                var cut = to - lower;
                kept.Add(strip with { Start = strip.Start + cut, Top = to });
            }
        }

        kept.Add(new Strip(frame, from - top, to - top, from));
        kept.Sort((a, b) => a.Top.CompareTo(b.Top));
        _strips = kept;
    }

    private void Shift(int amount)
    {
        _strips = [.. _strips.Select(s => s with { Top = s.Top + amount })];
        CanvasHeight += amount;
        Position += amount;
    }
}
