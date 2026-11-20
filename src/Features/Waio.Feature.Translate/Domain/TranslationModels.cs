namespace Waio.Feature.Translate.Domain;

/// <summary>一次翻译请求(发给单个翻译源)。MAIO <c>TranslationRequest</c>。</summary>
/// <param name="Source">源语言;<see cref="Language.Auto"/> 表示交给翻译源自己识别。</param>
/// <param name="Detected">本地识别出的原文语言。</param>
internal sealed record TranslationRequest(string Text, Language Source, Language Target, Language? Detected = null)
{
    /// <summary>目标语言代码;不支持时抛"不支持"。</summary>
    public string TargetCode(Func<Language, string?> map) =>
        Target != Language.Auto && map(Target) is { } code ? code : throw TranslationException.Unsupported(Target);

    /// <summary>源语言代码(<see cref="Language.Auto"/> 映射成什么由各翻译源决定),不支持时抛错。</summary>
    public string SourceCode(Func<Language, string?> map) => map(Source) ?? throw TranslationException.Unsupported(Source);
}

/// <summary>查单词时的词典信息:音标 + 按词性分组的释义(只有部分翻译源提供)。</summary>
internal sealed record DictionaryEntry(IReadOnlyList<string> Phonetics, IReadOnlyList<DictionaryMeaning> Meanings)
{
    public bool IsEmpty => Phonetics.Count == 0 && Meanings.Count == 0;
}

internal sealed record DictionaryMeaning(string PartOfSpeech, IReadOnlyList<string> Terms);

/// <summary>单个翻译源的结果。</summary>
internal sealed record TranslationResult(string Text, DictionaryEntry? Dictionary = null);

/// <summary>翻译源抛出的错误,<see cref="Exception.Message"/> 直接显示在结果卡片上(MAIO <c>TranslationError</c>)。</summary>
internal sealed class TranslationException(string message) : Exception(message)
{
    public static TranslationException MissingSecret(string field) => new($"还没有填写 {field},请在设置 → 翻译 → 翻译源中填写");

    public static TranslationException Unsupported(Language language) => new($"这个翻译源不支持{language.DisplayName()}");

    public static TranslationException Http(int status, string body) =>
        new($"请求失败(HTTP {status}){(body.Length == 0 ? "" : ":" + body)}");

    public static TranslationException BadResponse(string detail) => new($"无法解析返回结果:{detail}");
}

/// <summary>翻译源。实现放在 Infrastructure/Providers/,每个源一个文件。</summary>
internal interface ITranslationProvider
{
    /// <summary>翻译。支持逐字输出的源(大模型)每收到一段就调用 <paramref name="onPartial"/>,参数是目前为止的完整译文。</summary>
    Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken);
}

/// <summary>
/// 大模型翻译的提示词(MAIO <c>PromptTemplate</c>,默认提示词思路来自 pot-desktop)。占位符:<c>$text</c> 原文,<c>$to</c> 目标语言,<c>$from</c> 源语言(英文名)。
/// </summary>
internal sealed record PromptTemplate(string System, string User)
{
    public static PromptTemplate Default { get; } = new(
        "You are a professional translation engine. Translate the text into fluent, natural and accurate "
        + "$to, without the style of machine translation. Keep the original formatting, line breaks, code, "
        + "URLs and proper nouns. Only output the translation; never explain, answer or add notes.",
        "Translate the following text into $to:\n\"\"\"\n$text\n\"\"\"");

    public (string System, string User) Render(TranslationRequest request) => (Fill(System, request), Fill(User, request));

    private static string Fill(string template, TranslationRequest request) => template
        .Replace("$to", request.Target.EnglishName(), StringComparison.Ordinal)
        .Replace("$from", request.Source.EnglishName(), StringComparison.Ordinal)
        .Replace("$text", request.Text, StringComparison.Ordinal);
}
