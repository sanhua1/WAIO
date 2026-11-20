using System.Globalization;
using System.Text;

namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 一个条目可以被搜到的名称,建目录时预先处理好(搜索时不再转换)。思路同 MAIO 的 SearchKeys。
/// <para>
/// 每个名称拆成若干"词":英文按空格、符号、大小写变化、字母和数字的交界拆开;每个汉字单独一个词。
/// 每个词有几种写法:英文词就是它自己;汉字是字本身加上各个拼音读音。
/// 匹配时每个词可以写全、只写第一个字母、或者(最后一个词)只写开头,所以
/// <c>wx</c>、<c>weixin</c>、<c>wxin</c>、<c>微x</c> 都能找到"微信",<c>vsc</c> 能找到"Visual Studio Code"。
/// </para>
/// </summary>
internal sealed class SearchKeys
{
    public static SearchKeys Empty { get; } = new([]);

    private SearchKeys(IReadOnlyList<SearchName> names) => Names = names;

    public IReadOnlyList<SearchName> Names { get; }

    /// <summary>多个名称(显示名、程序文件名、包名……),去掉重复和空的。</summary>
    public static SearchKeys From(IEnumerable<string?> rawNames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<SearchName>();
        foreach (var raw in rawNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var text = Normalize(raw);
            if (text.Length > 0 && seen.Add(text))
            {
                names.Add(new SearchName(text, Tokenize(raw)));
            }
        }

        return names.Count == 0 ? Empty : new SearchKeys(names);
    }

    /// <summary>搜索用的统一形式:Unicode NFC、小写、连续空白合成一个空格、去掉首尾空白。</summary>
    public static string Normalize(string text)
    {
        var lower = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        var pendingSpace = false;
        foreach (var c in lower)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>拆词。大小写变化要看原文,所以在转小写之前拆。</summary>
    public static IReadOnlyList<string[]> Tokenize(string raw)
    {
        var tokens = new List<string[]>();
        var word = new StringBuilder();
        Rune? last = null;

        void Flush()
        {
            if (word.Length > 0)
            {
                tokens.Add([word.ToString().ToLowerInvariant()]);
                word.Clear();
            }

            last = null;
        }

        foreach (var rune in raw.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (rune.IsBmp && Pinyin.IsHan(rune.Value))
            {
                Flush();
                var han = (char)rune.Value;
                var forms = new List<string> { han.ToString() };
                forms.AddRange(Pinyin.Readings(han));
                tokens.Add([.. forms]);
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                if (last is { } previous && IsBoundary(previous, rune))
                {
                    Flush();
                }

                word.Append(rune.ToString());
                last = rune;
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>词的分界:小写 → 大写(studioCode)、字母 ↔ 数字(Office2024)。</summary>
    private static bool IsBoundary(Rune previous, Rune next) =>
        (Rune.IsLower(previous) && Rune.IsUpper(next)) || Rune.IsDigit(previous) != Rune.IsDigit(next);
}

/// <param name="Text">小写、NFC 的整个名称。</param>
/// <param name="Tokens">每个词的各种写法(都是小写)。</param>
internal sealed record SearchName(string Text, IReadOnlyList<string[]> Tokens);

internal static class CultureText
{
    /// <summary>界面排序用的比较(中文按拼音顺序)。</summary>
    public static readonly StringComparer Ordering = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
}
