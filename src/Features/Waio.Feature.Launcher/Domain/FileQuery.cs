using System.Text;

namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 文件搜索的输入:空格分开的几个词,都要出现,不区分大小写;双引号括起来的算一个词(可以含空格)。
/// 含路径分隔符(<c>\</c>,<c>/</c> 当成 <c>\</c>)或盘符冒号的词按完整路径匹配,其余的词只比名称
/// ——同 Everything 默认的 <c>match_path_when_search_contains_path_separator=1</c>(按词判断,如 <c>\work order</c>)。
/// Everything 的其余语法(通配符、|、!、ext: 等)在 P3 做。
/// 词都折叠成小写 UTF-8,和索引里的名称块同一种形式(<see cref="FoldedText"/>)。
/// </summary>
internal sealed class FileQuery
{
    private FileQuery(IReadOnlyList<byte[]> terms, IReadOnlyList<byte[]> pathTerms, bool foldersOnly, string text)
    {
        Terms = terms;
        PathTerms = pathTerms;
        FoldersOnly = foldersOnly;
        Text = text;
        Primary = terms.Count == 0 ? null : terms.OrderByDescending(t => t.Length).First();
    }

    /// <summary>只比名称的词。</summary>
    public IReadOnlyList<byte[]> Terms { get; }

    /// <summary>和完整路径(如 <c>c:\users\x\a.txt</c>,折叠成小写)比的词。</summary>
    public IReadOnlyList<byte[]> PathTerms { get; }

    /// <summary>最长的名称词:先用它在名称块里粗筛。没有名称词(只有路径词)时为 null,改成逐条检查。</summary>
    public byte[]? Primary { get; }

    public bool FoldersOnly { get; }

    public string Text { get; }

    /// <summary>只有一个由字母数字组成的名称词时,同时按拼音匹配含汉字的名称(同 MAIO:<c>ht</c> 能找到"合同.docx")。</summary>
    public bool UsesPinyin => Terms.Count == 1 && Primary!.All(b => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9');

    /// <summary>没有可搜的词时返回 null。</summary>
    public static FileQuery? Parse(string text, bool foldersOnly)
    {
        var terms = new List<byte[]>();
        var pathTerms = new List<byte[]>();
        foreach (var token in Tokenize(text))
        {
            var isPath = token.AsSpan().IndexOfAny('\\', '/', ':') >= 0;
            var folded = FoldedText.Encode(isPath ? token.Replace('/', '\\') : token);
            var list = isPath ? pathTerms : terms;
            if (folded.Length > 0 && !list.Contains(folded, ByteArrayComparer.Instance))
            {
                list.Add(folded);
            }
        }

        return terms.Count + pathTerms.Count == 0 ? null : new FileQuery(terms, pathTerms, foldersOnly, text);
    }

    /// <summary>按空白分词;双引号里的空白不分开,引号本身去掉(没配对的引号到结尾为止)。</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}

/// <summary>索引和查询共用的"折叠"形式:逐字符 ToLowerInvariant(不做 NFC:NTFS 上的名称本来就是用户输入的形式),再编成 UTF-8。</summary>
internal static class FoldedText
{
    public static byte[] Encode(string text)
    {
        Span<char> folded = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        Fold(text, folded);
        return Encoding.UTF8.GetBytes(folded.ToArray());
    }

    /// <summary>折叠到 <paramref name="destination"/>(和原文一样长);返回是否和原文不同。</summary>
    public static bool Fold(ReadOnlySpan<char> source, Span<char> destination)
    {
        var changed = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var lower = c < 128 ? (char)(c is >= 'A' and <= 'Z' ? c + 32 : c) : char.ToLowerInvariant(c);
            destination[i] = lower;
            changed |= lower != c;
        }

        return changed;
    }
}

/// <summary>
/// 文件名和查询词的匹配质量(规则同 MAIO 的 NameMatch,其出处是 oil-find 的 sift_match_quality,MIT):
/// 4 = 完全相同或去掉扩展名后相同;3 = 名称开头;2 = 某个词的开头;1 = 包含;0 = 不包含。
/// </summary>
internal static class NameMatch
{
    public static int Quality(ReadOnlySpan<byte> name, ReadOnlySpan<byte> term)
    {
        if (term.Length == 0 || term.Length > name.Length)
        {
            return 0;
        }

        var best = 0;
        var from = 0;
        while (from <= name.Length - term.Length)
        {
            var hit = name[from..].IndexOf(term);
            if (hit < 0)
            {
                break;
            }

            var index = from + hit;
            best = Math.Max(best, QualityAt(name, index, term.Length));
            if (best >= 2)
            {
                break;
            }

            from = index + 1;
        }

        return best;
    }

    private static int QualityAt(ReadOnlySpan<byte> name, int index, int length)
    {
        if (index == 0)
        {
            var rest = name[length..];
            return rest.Length == 0 || (rest[0] == (byte)'.' && rest[1..].IndexOf((byte)'.') < 0) ? 4 : 3;
        }

        return IsBoundary(name[index - 1], name[index]) ? 2 : 1;
    }

    private static bool IsBoundary(byte previous, byte current)
    {
        if (previous is (byte)' ' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'(' or (byte)'[' or (byte)'+' or (byte)',')
        {
            return true;
        }

        static bool Digit(byte b) => b is >= (byte)'0' and <= (byte)'9';
        static bool Letter(byte b) => b is >= (byte)'a' and <= (byte)'z';
        return (previous < 0x80 && current >= 0x80) || (Digit(previous) && Letter(current)) || (Letter(previous) && Digit(current));
    }
}
