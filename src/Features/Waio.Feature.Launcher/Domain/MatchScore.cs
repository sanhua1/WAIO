namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 输入和名称的匹配质量。分数越高越靠前;null 表示不匹配。档次与 MAIO 的 MatchScore 相同:
/// <list type="table">
/// <item><term>100</term><description>名称完全相同</description></item>
/// <item><term>90</term><description>名称开头</description></item>
/// <item><term>75</term><description>从第一个词开始按词匹配(<c>wx</c> → 微信、<c>vsc</c> → Visual Studio Code)</description></item>
/// <item><term>60</term><description>从中间某个词开始按词匹配(<c>code</c> → Visual Studio Code)</description></item>
/// <item><term>50</term><description>名称包含</description></item>
/// <item><term>20</term><description>按顺序出现(<c>slk</c> → Slack,至少两个字符)</description></item>
/// </list>
/// 输入里有空格时,先按整体匹配;不行就要求每一段都匹配,取最低的一档再减 5。
/// </summary>
internal static class MatchScore
{
    public static int? Score(string query, SearchKeys keys)
    {
        var normalized = SearchKeys.Normalize(query);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (Best(normalized, keys) is { } whole)
        {
            return whole;
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var lowest = int.MaxValue;
        foreach (var part in parts)
        {
            if (Best(part, keys) is not { } value)
            {
                return null;
            }

            lowest = Math.Min(lowest, value);
        }

        return lowest - 5;
    }

    private static int? Best(string query, SearchKeys keys)
    {
        int? best = null;
        foreach (var name in keys.Names)
        {
            if (Score(query, name) is { } value && (best is null || value > best))
            {
                best = value;
            }
        }

        return best;
    }

    /// <param name="query">已经 <see cref="SearchKeys.Normalize"/> 过的输入。</param>
    public static int? Score(string query, SearchName name)
    {
        if (name.Text == query)
        {
            return 100;
        }

        if (name.Text.StartsWith(query, StringComparison.Ordinal))
        {
            return 90;
        }

        var compact = query.Replace(" ", "", StringComparison.Ordinal);
        if (TokenMatchStart(compact, name.Tokens) is { } start)
        {
            return start == 0 ? 75 : 60;
        }

        if (name.Text.Contains(query, StringComparison.Ordinal))
        {
            return 50;
        }

        if (compact.Length >= 2 && IsSubsequence(compact, name.Text))
        {
            return 20;
        }

        return null;
    }

    /// <summary>从哪个词开始能按词匹配完整个输入(最早的那个);都不行返回 null。</summary>
    public static int? TokenMatchStart(string query, IReadOnlyList<string[]> tokens)
    {
        if (query.Length == 0)
        {
            return null;
        }

        for (var start = 0; start < tokens.Count; start++)
        {
            var memo = new Dictionary<int, bool>();
            if (Matches(query, 0, tokens, start, memo))
            {
                return start;
            }
        }

        return null;
    }

    /// <summary>
    /// 输入从 <paramref name="position"/> 起,能否由第 <paramref name="token"/> 个词开始的连续几个词拼出来。
    /// 每个词可以写全,或只写第一个字母;最后剩下的输入可以只是某个词的开头。
    /// </summary>
    private static bool Matches(string query, int position, IReadOnlyList<string[]> tokens, int token, Dictionary<int, bool> memo)
    {
        if (position == query.Length)
        {
            return true;
        }

        if (token >= tokens.Count)
        {
            return false;
        }

        var key = (position * 1024) + token;
        if (memo.TryGetValue(key, out var known))
        {
            return known;
        }

        var result = false;
        var rest = query.AsSpan(position);
        foreach (var form in tokens[token])
        {
            if (result)
            {
                break;
            }

            if (form.Length == 0 || form[0] != query[position])
            {
                continue;
            }

            if (form.AsSpan().StartsWith(rest, StringComparison.Ordinal))
            {
                result = true; // 剩下的输入是这个词的开头
            }
            else if (rest.StartsWith(form, StringComparison.Ordinal))
            {
                result = Matches(query, position + form.Length, tokens, token + 1, memo); // 写全了这个词
            }

            if (!result && form.Length > 1)
            {
                result = Matches(query, position + 1, tokens, token + 1, memo); // 只写了首字母
            }
        }

        memo[key] = result;
        return result;
    }

    private static bool IsSubsequence(string query, string text)
    {
        var index = 0;
        foreach (var c in text)
        {
            if (index < query.Length && c == query[index])
            {
                index++;
            }
        }

        return index == query.Length;
    }
}
