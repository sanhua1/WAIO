namespace Waio.Feature.Translate.Domain;

/// <summary>翻译语言(参考 MAIO <c>Domain/Language.swift</c>)。<see cref="Languages.Code"/> 是 BCP 47 代码,各翻译源再映射成自己的代码。</summary>
internal enum Language
{
    Auto,
    SimplifiedChinese,
    TraditionalChinese,
    English,
    Japanese,
    Korean,
    French,
    German,
    Spanish,
    Russian,
    Italian,
    Portuguese,
    Vietnamese,
    Thai,
    Arabic,
    Indonesian,
    Malay,
    Turkish,
    Polish,
    Dutch,
    Ukrainian,
}

internal static class Languages
{
    public static IReadOnlyList<Language> All { get; } = Enum.GetValues<Language>();

    /// <summary>可以作为目标语言的(不含"自动")。</summary>
    public static IReadOnlyList<Language> Targets { get; } = [.. All.Where(l => l != Language.Auto)];

    public static string Code(this Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh-Hans",
        Language.TraditionalChinese => "zh-Hant",
        Language.English => "en",
        Language.Japanese => "ja",
        Language.Korean => "ko",
        Language.French => "fr",
        Language.German => "de",
        Language.Spanish => "es",
        Language.Russian => "ru",
        Language.Italian => "it",
        Language.Portuguese => "pt",
        Language.Vietnamese => "vi",
        Language.Thai => "th",
        Language.Arabic => "ar",
        Language.Indonesian => "id",
        Language.Malay => "ms",
        Language.Turkish => "tr",
        Language.Polish => "pl",
        Language.Dutch => "nl",
        _ => "uk",
    };

    public static string DisplayName(this Language language) => language switch
    {
        Language.Auto => "自动检测",
        Language.SimplifiedChinese => "简体中文",
        Language.TraditionalChinese => "繁体中文",
        Language.English => "英语",
        Language.Japanese => "日语",
        Language.Korean => "韩语",
        Language.French => "法语",
        Language.German => "德语",
        Language.Spanish => "西班牙语",
        Language.Russian => "俄语",
        Language.Italian => "意大利语",
        Language.Portuguese => "葡萄牙语",
        Language.Vietnamese => "越南语",
        Language.Thai => "泰语",
        Language.Arabic => "阿拉伯语",
        Language.Indonesian => "印尼语",
        Language.Malay => "马来语",
        Language.Turkish => "土耳其语",
        Language.Polish => "波兰语",
        Language.Dutch => "荷兰语",
        _ => "乌克兰语",
    };

    /// <summary>写进大模型提示词的英文名。</summary>
    public static string EnglishName(this Language language) => language switch
    {
        Language.Auto => "the detected language",
        Language.SimplifiedChinese => "Simplified Chinese",
        Language.TraditionalChinese => "Traditional Chinese",
        Language.English => "English",
        Language.Japanese => "Japanese",
        Language.Korean => "Korean",
        Language.French => "French",
        Language.German => "German",
        Language.Spanish => "Spanish",
        Language.Russian => "Russian",
        Language.Italian => "Italian",
        Language.Portuguese => "Portuguese",
        Language.Vietnamese => "Vietnamese",
        Language.Thai => "Thai",
        Language.Arabic => "Arabic",
        Language.Indonesian => "Indonesian",
        Language.Malay => "Malay",
        Language.Turkish => "Turkish",
        Language.Polish => "Polish",
        Language.Dutch => "Dutch",
        _ => "Ukrainian",
    };

    public static bool IsChinese(this Language language) =>
        language is Language.SimplifiedChinese or Language.TraditionalChinese;
}

/// <summary>
/// 目标语言规则(MAIO <c>TargetLanguagePolicy</c>):原文已经是目标语言时,改为翻译成第二目标语言(例如中文 → 英文,其他 → 中文)。
/// </summary>
internal static class TargetLanguagePolicy
{
    public static Language Target(Language? detected, Language preferred, Language second)
    {
        if (detected is not { } known || known == Language.Auto)
        {
            return preferred;
        }

        return known == preferred || (known.IsChinese() && preferred.IsChinese()) ? second : preferred;
    }
}
