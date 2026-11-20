namespace Waio.Feature.Translate.Domain;

/// <summary>
/// 翻译源类型(MAIO <c>ProviderKind</c>)。新增一个源:这里加一个值和 spec,在 Infrastructure/Providers/ 加实现,在 ProviderFactory 注册。
/// MAIO 的 Apple 翻译(macOS 系统框架)在 Windows 上没有对应物,不提供。
/// </summary>
internal enum ProviderKind
{
    Google,
    Bing,
    Transmart,
    OpenAICompatible,
    DeepL,
    Baidu,
    Youdao,
    Tencent,
    Alibaba,
    Volcengine,
    Caiyun,
    Niutrans,
}

internal enum FieldKind
{
    Text,

    /// <summary>存进密钥存储(DPAPI,ADR-0014),不写进设置文件。</summary>
    Secret,

    Multiline,
}

/// <summary>翻译源的一个配置项。</summary>
internal sealed record ProviderField(
    string Key, string Title, FieldKind Kind = FieldKind.Text, string Placeholder = "", string DefaultValue = "", bool IsRequired = true);

internal enum ProviderCost
{
    /// <summary>免费、不需要 Key(非官方接口,可能失效)。</summary>
    Free,

    /// <summary>需要 Key,有免费额度。</summary>
    FreeQuota,

    /// <summary>需要 Key,按量付费。</summary>
    Paid,
}

/// <summary>翻译源的静态说明:名称、费用、配置项(设置界面据此自动生成表单)。</summary>
/// <param name="Mark">卡片和列表里的小图标文字(MAIO 用 SF Symbols 的字母圆圈,这里画成字母徽标)。</param>
/// <param name="AllowsMultiple">能否同时存在多个实例(例如多个大模型服务)。</param>
internal sealed record ProviderSpec(
    string DisplayName,
    string Mark,
    ProviderCost Cost,
    string Summary,
    string? SignupUrl = null,
    IReadOnlyList<ProviderField>? Fields = null,
    bool AllowsMultiple = false)
{
    public IReadOnlyList<ProviderField> FieldList => Fields ?? [];
}

/// <summary>每种翻译源的说明与配置项(MAIO <c>ProviderCatalog.swift</c>,文案一致)。</summary>
internal static class ProviderCatalog
{
    public static IReadOnlyList<ProviderKind> All { get; } = Enum.GetValues<ProviderKind>();

    public static ProviderSpec Spec(this ProviderKind kind) => kind switch
    {
        ProviderKind.Google => new("Google 翻译", "G", ProviderCost.Free, "免费,质量好,查单词时显示音标和释义。国内需要代理。",
            Fields: [new("baseURL", "服务地址", Placeholder: "https://translate.googleapis.com",
                DefaultValue: "https://translate.googleapis.com", IsRequired: false)]),
        ProviderKind.Bing => new("必应翻译", "B", ProviderCost.Free, "免费,国内可直连。使用必应网页翻译器的接口。"),
        ProviderKind.Transmart => new("腾讯交互翻译", "T", ProviderCost.Free, "免费,国内可直连(transmart.qq.com)。"),
        ProviderKind.OpenAICompatible => OpenAISpec,
        ProviderKind.DeepL => new("DeepL", "D", ProviderCost.FreeQuota,
            "免费版 API 每月 50 万字符。Key 以 :fx 结尾时自动使用免费版地址。也可以填 DeepLX 地址。",
            "https://www.deepl.com/pro-api",
            [new("authKey", "Auth Key", FieldKind.Secret, IsRequired: false),
             new("customURL", "自定义地址(DeepLX)", Placeholder: "留空使用官方 API", IsRequired: false)]),
        ProviderKind.Baidu => new("百度翻译", "百", ProviderCost.FreeQuota, "通用文本翻译,认证后每月有免费额度。",
            "https://fanyi-api.baidu.com/manage/developer",
            [new("appID", "APP ID"), new("secret", "密钥", FieldKind.Secret)]),
        ProviderKind.Youdao => new("有道翻译", "有", ProviderCost.FreeQuota, "有道智云文本翻译,新用户有体验额度。",
            "https://ai.youdao.com/console/",
            [new("appKey", "应用 ID"), new("appSecret", "应用密钥", FieldKind.Secret)]),
        ProviderKind.Tencent => new("腾讯云翻译", "腾", ProviderCost.FreeQuota, "腾讯云机器翻译,每月有免费额度。",
            "https://console.cloud.tencent.com/cam/capi",
            [new("secretID", "SecretId"), new("secretKey", "SecretKey", FieldKind.Secret),
             new("region", "地域", DefaultValue: "ap-beijing", IsRequired: false)]),
        ProviderKind.Alibaba => new("阿里翻译", "阿", ProviderCost.FreeQuota, "阿里云机器翻译(通用版),每月有免费额度。",
            "https://ram.console.aliyun.com/manage/ak",
            [new("accessKeyID", "AccessKey ID"), new("accessKeySecret", "AccessKey Secret", FieldKind.Secret)]),
        ProviderKind.Volcengine => new("火山翻译", "火", ProviderCost.FreeQuota, "火山引擎机器翻译,每月有免费额度。",
            "https://console.volcengine.com/iam/keymanage/",
            [new("accessKeyID", "Access Key ID"), new("secretAccessKey", "Secret Access Key", FieldKind.Secret)]),
        ProviderKind.Caiyun => new("彩云小译", "彩", ProviderCost.FreeQuota, "彩云小译 API,新用户有免费额度。",
            "https://dashboard.caiyunapp.com/", [new("token", "Token", FieldKind.Secret)]),
        _ => new("小牛翻译", "牛", ProviderCost.FreeQuota, "小牛翻译开放平台,每天有免费额度。",
            "https://niutrans.com/cloud/account_info/info", [new("apiKey", "API Key", FieldKind.Secret)]),
    };

    private static readonly ProviderSpec OpenAISpec = new(
        "大模型(OpenAI 兼容)", "AI", ProviderCost.Paid,
        "DeepSeek、OpenAI、Gemini、智谱、Kimi、通义、硅基流动、Ollama 等兼容 OpenAI 接口的服务。译文逐字显示。",
        Fields:
        [
            new("baseURL", "接口地址", Placeholder: "https://api.deepseek.com"),
            new("model", "模型", Placeholder: "deepseek-chat"),
            new("apiKey", "API Key", FieldKind.Secret, IsRequired: false),
            new("systemPrompt", "系统提示词", FieldKind.Multiline, DefaultValue: PromptTemplate.Default.System, IsRequired: false),
            new("userPrompt", "用户提示词", FieldKind.Multiline, DefaultValue: PromptTemplate.Default.User, IsRequired: false),
        ],
        AllowsMultiple: true);
}
