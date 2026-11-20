using System.Globalization;
using Waio.Kit.Text;

namespace Waio.Feature.Translate.Domain;

/// <summary>
/// 本地语种识别(离线、零延迟)。结果用于决定目标语言(<see cref="TargetLanguagePolicy"/>)。
/// <para>
/// MAIO 用 macOS 的 NLLanguageRecognizer;Windows 没有对应的现成框架,这里按<b>文字系统</b>判断:
/// 假名→日语、谚文→韩语、汉字→中文(常见繁体字多于简体字时算繁体)、西里尔→俄语(有 і ї є ґ 算乌克兰语)、泰文、阿拉伯文;
/// 拉丁字母按特有字母粗分(ñ¿¡ 西班牙、ß 德语、ç/é 等法语……),都没有就算英语。
/// 拉丁语系之间的区分只是粗略估计——它只影响"原文已是目标语言时改译成第二目标语言"这一条规则。
/// </para>
/// </summary>
internal static class LanguageDetector
{
    // 常用字里简繁写法不同的一小组,用来区分简体和繁体
    private const string Traditional = "這個們來時會說對國過還發後學實現點經從樣問題體關機開進動應長義華處萬與當業務電話認為麼讓書";
    private const string Simplified = "这个们来时会说对国过还发后学实现点经从样问题体关机开进动应长义华处万与当业务电话认为么让书";

    public static Language? Detect(string text)
    {
        var sample = text.Length > 500 ? text[..500] : text;
        int han = 0, kana = 0, hangul = 0, cyrillic = 0, ukrainian = 0, thai = 0, arabic = 0, latin = 0, trad = 0, simp = 0;
        foreach (var c in sample)
        {
            switch (c)
            {
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ': hangul++; break;
                case >= '一' and <= '鿿' or >= '㐀' and <= '䶿':
                    han++;
                    trad += Traditional.Contains(c) ? 1 : 0;
                    simp += Simplified.Contains(c) ? 1 : 0;
                    break;
                case >= 'Ѐ' and <= 'ӿ':
                    cyrillic++;
                    ukrainian += "іїєґІЇЄҐ".Contains(c) ? 1 : 0;
                    break;
                case >= '฀' and <= '๿': thai++; break;
                case >= '؀' and <= 'ۿ': arabic++; break;
                default:
                    if (char.IsLetter(c) && c < 'ɐ') // 基本拉丁 + 拉丁扩展 A/B
                    {
                        latin++;
                    }

                    break;
            }
        }

        if (kana > 0 && kana * 5 >= han)
        {
            return Language.Japanese; // 日文必然夹着假名;纯汉字按中文算
        }

        var max = new[] { han, hangul, cyrillic, thai, arabic, latin }.Max();
        if (max == 0)
        {
            return null;
        }

        if (max == han)
        {
            return trad > simp ? Language.TraditionalChinese : Language.SimplifiedChinese;
        }

        if (max == hangul) { return Language.Korean; }
        if (max == cyrillic) { return ukrainian > 0 ? Language.Ukrainian : Language.Russian; }
        if (max == thai) { return Language.Thai; }
        if (max == arabic) { return Language.Arabic; }
        return Latin(sample);
    }

    private static Language Latin(string text)
    {
        var lower = text.ToLower(CultureInfo.InvariantCulture);
        if (lower.IndexOfAny(['ñ', '¿', '¡']) >= 0) { return Language.Spanish; }
        if (lower.Contains('ß') || lower.IndexOfAny(['ä', 'ö', 'ü']) >= 0 && !lower.Contains('ç')) { return Language.German; }
        if (lower.IndexOfAny(['ã', 'õ']) >= 0) { return Language.Portuguese; }
        if (lower.IndexOfAny(['ç', 'é', 'è', 'ê', 'à', 'ù', 'œ']) >= 0) { return Language.French; }
        if (lower.IndexOfAny(['ą', 'ę', 'ł', 'ń', 'ś', 'ź', 'ż']) >= 0) { return Language.Polish; }
        if (lower.IndexOfAny(['ğ', 'ş', 'ı']) >= 0) { return Language.Turkish; }
        if (lower.IndexOfAny(['ơ', 'ư', 'đ', 'ạ', 'ả', 'ấ', 'ầ', 'ế', 'ệ', 'ộ']) >= 0) { return Language.Vietnamese; }
        return Language.English;
    }
}

/// <summary>一条翻译历史:原文 + 各翻译源的译文(MAIO <c>HistoryEntry</c>)。</summary>
internal sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset Date { get; set; }

    public string Text { get; set; } = "";

    public Language Source { get; set; }

    public Language Target { get; set; }

    public List<HistoryItem> Results { get; set; } = [];

    public bool Matches(string query)
    {
        var q = query.Trim();
        return q.Length == 0
            || Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || Results.Any(r => (r.Text ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }
}

internal sealed record HistoryItem(string Service, string Text);

/// <summary>历史记录列表的纯逻辑:新的在前,同样的原文(且同一目标语言)只保留最新一条,超过上限丢弃最旧的。</summary>
internal static class HistoryList
{
    public static List<HistoryEntry> Adding(HistoryEntry entry, IEnumerable<HistoryEntry> entries, int limit)
    {
        var result = entries.Where(e => e.Text != entry.Text || e.Target != entry.Target).ToList();
        result.Insert(0, entry);
        return [.. result.Take(Math.Max(1, limit))];
    }
}
