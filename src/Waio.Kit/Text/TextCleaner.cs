using System.Text;

namespace Waio.Kit.Text;

/// <summary>
/// 删除多余换行(MAIO <c>MAIOKit/Support/TextCleaner</c>,规则一致):把从 PDF 等处复制来的断行文字拼回完整段落。
/// 空行(段落分隔)保留;行尾连字符断开的单词拼回;中日韩文字之间直接拼接,其他文字之间加空格。
/// 翻译和截图识别文字共用,所以放在 Kit(ADR-0018)。
/// </summary>
public static class TextCleaner
{
    /// <summary>删除段落内的换行,保留空行分隔的段落。</summary>
    public static string RemovingLineBreaks(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Join("\n\n", SplitParagraphs(normalized).Select(JoinLines));
    }

    /// <summary>是否中日韩文字(含全角标点);这类文字之间不加空格。</summary>
    public static bool IsCjk(char c) => c switch
    {
        >= '　' and <= '〿' or >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿'
            or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯' => true,
        _ => false,
    };

    private static List<List<string>> SplitParagraphs(string text)
    {
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim(' ', '\t');
            if (trimmed.Length == 0)
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(current);
                    current = [];
                }
            }
            else
            {
                current.Add(trimmed);
            }
        }

        if (current.Count > 0)
        {
            paragraphs.Add(current);
        }

        return paragraphs;
    }

    private static string JoinLines(List<string> lines)
    {
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            if (result.Length == 0)
            {
                result.Append(line);
                continue;
            }

            var last = result[^1];
            var first = line[0];
            if (last == '-' && result.Length >= 2 && char.IsLetter(result[^2]) && char.IsLower(first))
            {
                result.Length--;
                result.Append(line);
            }
            else if (IsCjk(last) || IsCjk(first))
            {
                result.Append(line);
            }
            else
            {
                result.Append(' ').Append(line);
            }
        }

        return result.ToString();
    }
}
