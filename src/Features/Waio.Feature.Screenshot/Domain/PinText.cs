namespace Waio.Feature.Screenshot.Domain;

/// <summary>识别出的一个字和它的横向范围(原图像素)。</summary>
public sealed record TextChar(string Value, double Left, double Right)
{
    public double Center => (Left + Right) / 2;
}

/// <summary>识别出的一段文字(一个文字框):位置 + 逐字范围。</summary>
public sealed record TextSegment(TextBounds Box, IReadOnlyList<TextChar> Chars)
{
    public string Text => string.Concat(Chars.Select(c => c.Value));
}

/// <summary>文字里的一个插入点:第几段、段内第几个字之前(0 ~ 字数)。</summary>
public readonly record struct TextPosition(int Segment, int Offset) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) => Segment != other.Segment ? Segment.CompareTo(other.Segment) : Offset.CompareTo(other.Offset);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;

    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;

    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// 贴图上可以选择的文字(同 PixPin:贴图后自动识别,鼠标在文字上拖动就是选择)。坐标都是原图像素(未旋转、未缩放)。
/// 段按阅读顺序排列(<see cref="OcrLayout.Rows{T}"/>:从上到下分行,同一行从左到右);复制时同一行的段用空格连接,行与行之间换行。
/// </summary>
public sealed class PinTextLayout
{
    /// <summary>判断"鼠标在文字上"时,文字框四周再放宽的距离(像素)。</summary>
    public const double HitSlop = 2;

    private readonly int[] _rowOf;

    public PinTextLayout(IEnumerable<TextSegment> segments)
    {
        var rows = OcrLayout.Rows(segments.Where(s => s.Chars.Count > 0), s => s.Box);
        Segments = [.. rows.SelectMany(r => r)];
        _rowOf = [.. rows.SelectMany((row, index) => row.Select(_ => index))];
    }

    public IReadOnlyList<TextSegment> Segments { get; }

    public bool IsEmpty => Segments.Count == 0;

    public TextPosition Start => new(0, 0);

    public TextPosition End => IsEmpty ? Start : new(Segments.Count - 1, Segments[^1].Chars.Count);

    /// <summary>点是不是落在某段文字上(鼠标变成 I 形、拖动是选择)。</summary>
    public bool Contains(double x, double y) => Segments.Any(s =>
        x >= s.Box.X - HitSlop && x <= s.Box.Right + HitSlop && y >= s.Box.Y - HitSlop && y <= s.Box.Bottom + HitSlop);

    /// <summary>
    /// 离点最近的插入点(拖动选择时用,点可以在文字外面):先找竖直方向包含它的段(没有就找竖直距离最近的那一行),
    /// 再在这些段里找横向最近的段;段内按字的中点决定插在哪个字前面。
    /// </summary>
    public TextPosition PositionAt(double x, double y)
    {
        if (IsEmpty)
        {
            return Start;
        }

        static double VerticalGap(TextBounds b, double y) => y < b.Y ? b.Y - y : y > b.Bottom ? y - b.Bottom : 0;
        static double HorizontalGap(TextBounds b, double x) => x < b.X ? b.X - x : x > b.Right ? x - b.Right : 0;
        var nearestRowGap = Segments.Min(s => VerticalGap(s.Box, y));
        var candidates = Enumerable.Range(0, Segments.Count).Where(i => VerticalGap(Segments[i].Box, y) <= nearestRowGap + 0.5);
        var index = candidates.MinBy(i => HorizontalGap(Segments[i].Box, x));
        var chars = Segments[index].Chars;
        var offset = 0;
        while (offset < chars.Count && x > chars[offset].Center)
        {
            offset++;
        }

        return new TextPosition(index, offset);
    }

    /// <summary>选中的文字(两端顺序无所谓)。</summary>
    public string Text(TextPosition a, TextPosition b)
    {
        var (from, to) = a <= b ? (a, b) : (b, a);
        var result = new System.Text.StringBuilder();
        for (var i = from.Segment; i <= to.Segment && i < Segments.Count; i++)
        {
            var chars = Segments[i].Chars;
            var start = i == from.Segment ? from.Offset : 0;
            var end = i == to.Segment ? to.Offset : chars.Count;
            if (i > from.Segment)
            {
                result.Append(_rowOf[i] == _rowOf[i - 1] ? " " : "\n");
            }

            for (var c = start; c < end; c++)
            {
                result.Append(chars[c].Value);
            }
        }

        return result.ToString().Trim(' ');
    }

    /// <summary>全部文字。</summary>
    public string AllText => Text(Start, End);

    /// <summary>选中部分要画的高亮矩形(每段一个,原图像素)。</summary>
    public List<TextBounds> Highlights(TextPosition a, TextPosition b)
    {
        var (from, to) = a <= b ? (a, b) : (b, a);
        var rects = new List<TextBounds>();
        for (var i = from.Segment; i <= to.Segment && i < Segments.Count; i++)
        {
            var segment = Segments[i];
            var start = i == from.Segment ? from.Offset : 0;
            var end = i == to.Segment ? to.Offset : segment.Chars.Count;
            if (end > start)
            {
                var left = segment.Chars[start].Left;
                rects.Add(new TextBounds(left, segment.Box.Y, segment.Chars[end - 1].Right - left, segment.Box.Height));
            }
        }

        return rects;
    }

    /// <summary>整段(双击选中一行)。</summary>
    public (TextPosition From, TextPosition To) SegmentRange(int segment) => (new(segment, 0), new(segment, Segments[segment].Chars.Count));
}
