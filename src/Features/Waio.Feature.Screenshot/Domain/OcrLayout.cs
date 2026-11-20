using Waio.Kit.Text;

namespace Waio.Feature.Screenshot.Domain;

/// <summary>文字在图片里的位置(像素,左上角原点)。</summary>
public readonly record struct TextBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double MidY => Y + Height / 2;

    public TextBounds Union(TextBounds other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new TextBounds(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

/// <summary>识别出的一行文字(MAIO RecognizedLine)。</summary>
public sealed record RecognizedLine(string Text, TextBounds Box);

/// <summary>识别出的一个词(Windows OCR 按词返回)。</summary>
public sealed record RecognizedWord(string Text, TextBounds Box);

/// <summary>
/// 把识别结果拼成文字(MAIO OCRLayout,规则一致):从上到下分行,同一行里(左右两栏、表格)从左到右用空格连接;
/// 行间距大于行高的 0.9 倍时认为是新段落,中间空一行("删除换行"按空行保留段落)。
/// </summary>
public static class OcrLayout
{
    /// <summary>行间距超过行高的这个倍数时算新段落。</summary>
    public const double ParagraphGap = 0.9;

    public static string Text(IEnumerable<RecognizedLine> lines)
    {
        var rows = Rows(lines.Where(l => l.Text.Trim().Length > 0));
        var result = new System.Text.StringBuilder();
        TextBounds? previous = null;
        foreach (var row in rows)
        {
            var frame = row.Select(l => l.Box).Aggregate((a, b) => a.Union(b));
            if (previous is { } last)
            {
                var height = Math.Min(last.Height, frame.Height);
                result.Append(frame.Y - last.Bottom > height * ParagraphGap ? "\n\n" : "\n");
            }

            result.AppendJoin(' ', row.Select(l => l.Text.Trim()));
            previous = frame;
        }

        return result.ToString();
    }

    /// <summary>分行:垂直方向的中心落在当前行范围内的,归到同一行。</summary>
    public static List<List<RecognizedLine>> Rows(IEnumerable<RecognizedLine> lines) => Rows(lines, l => l.Box);

    /// <summary>同上,适用于任何带文字框的东西(贴图上可选择的文字也按这个顺序)。</summary>
    public static List<List<T>> Rows<T>(IEnumerable<T> items, Func<T, TextBounds> boxOf)
    {
        var rows = new List<List<T>>();
        TextBounds band = default;
        foreach (var item in items.OrderBy(i => boxOf(i).Y))
        {
            var box = boxOf(item);
            if (rows.Count > 0 && box.MidY >= band.Y && box.MidY <= band.Bottom)
            {
                rows[^1].Add(item);
                band = band.Union(box);
            }
            else
            {
                rows.Add([item]);
                band = box;
            }
        }

        return [.. rows.Select(r => r.OrderBy(i => boxOf(i).X).ToList())];
    }

    /// <summary>
    /// 把一行里的词拼起来(Windows 特有,MAIO 的 Vision 直接给整行):Windows OCR 识别中文时每个字(词)之间都带空格,
    /// 所以两侧都是中日韩文字、或任一侧是全角标点时直接相连;其余情况(中英之间、英文单词之间)加一个空格。
    /// </summary>
    public static RecognizedLine JoinWords(IReadOnlyList<RecognizedWord> words)
    {
        var text = new System.Text.StringBuilder();
        TextBounds? box = null;
        foreach (var word in words)
        {
            var value = word.Text.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (text.Length > 0 && !NoSpaceBetween(text[^1], value[0]))
            {
                text.Append(' ');
            }

            text.Append(value);
            box = box is { } b ? b.Union(word.Box) : word.Box;
        }

        return new RecognizedLine(text.ToString(), box ?? default);
    }

    private static bool NoSpaceBetween(char left, char right) =>
        (TextCleaner.IsCjk(left) && TextCleaner.IsCjk(right)) || IsFullWidthPunctuation(left) || IsFullWidthPunctuation(right);

    /// <summary>中日韩标点(、。「」等)和全角符号(，！？：等)。</summary>
    private static bool IsFullWidthPunctuation(char c) =>
        c is (>= '　' and <= '〿') or (>= '＀' and <= '／') or (>= '：' and <= '＠')
            or (>= '［' and <= '｀') or (>= '｛' and <= '･');
}
