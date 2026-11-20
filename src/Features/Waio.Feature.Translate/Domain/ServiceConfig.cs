using System.Text.Json.Serialization;

namespace Waio.Feature.Translate.Domain;

/// <summary>
/// 一个翻译源实例(MAIO <c>ServiceConfig</c>;同一种源可以有多个,例如多个大模型服务)。
/// 非敏感配置存在 <see cref="Options"/>;密钥存在密钥存储,名字为 <c>&lt;Id&gt;.&lt;字段 key&gt;</c>。
/// 设置文件是不可信输入:字段可能缺失或为 null,读的时候都要兜底。
/// </summary>
internal sealed class ServiceConfig
{
    public string Id { get; set; } = "";

    [JsonIgnore]
    internal ProviderKind Kind { get; set; }

    /// <summary>序列化用(JSON 里是 "kind": "google");不认识的类型当作 Google。</summary>
    [JsonPropertyName("kind")]
    public string KindName
    {
        get => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(Kind.ToString());
        set => Kind = Enum.TryParse<ProviderKind>(value, ignoreCase: true, out var kind) ? kind : ProviderKind.Google;
    }

    public string Name { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public Dictionary<string, string> Options { get; set; } = [];

    [JsonIgnore]
    internal ProviderSpec Spec => Kind.Spec();

    /// <summary>取配置值,没填时用默认值。</summary>
    internal string Option(string key)
    {
        var value = (Options?.GetValueOrDefault(key) ?? "").Trim();
        return value.Length > 0 ? value : Spec.FieldList.FirstOrDefault(f => f.Key == key)?.DefaultValue ?? "";
    }

    internal string SecretKey(string field) => $"{Id}.{field}";

    internal static ServiceConfig Make(ProviderKind kind, string id, bool isEnabled = true) =>
        new() { Id = id, Kind = kind, Name = kind.Spec().DisplayName, IsEnabled = isEnabled };

    /// <summary>
    /// 出厂配置(MAIO):三个免费源默认启用;DeepSeek 需要 Key,默认关闭。
    /// (MAIO 还有默认关闭的 Apple 翻译,Windows 上没有。)
    /// </summary>
    internal static List<ServiceConfig> Defaults() =>
    [
        Make(ProviderKind.Google, "google"),
        Make(ProviderKind.Bing, "bing"),
        Make(ProviderKind.Transmart, "transmart"),
        LlmPreset.DeepSeek.MakeConfig("deepseek", isEnabled: false),
    ];

    internal ServiceConfig Clone() => new()
    {
        Id = Id, Kind = Kind, Name = Name, IsEnabled = IsEnabled, Options = new Dictionary<string, string>(Options ?? []),
    };

    internal static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>大模型服务预设:只是帮用户填好地址和模型,之后可以随意修改(MAIO <c>LLMPreset</c>)。</summary>
internal sealed record LlmPreset(string Name, string BaseUrl, string Model, string SignupUrl)
{
    public static LlmPreset DeepSeek { get; } =
        new("DeepSeek", "https://api.deepseek.com", "deepseek-chat", "https://platform.deepseek.com/api_keys");

    public static IReadOnlyList<LlmPreset> All { get; } =
    [
        DeepSeek,
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "https://platform.openai.com/api-keys"),
        new("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", "https://aistudio.google.com/apikey"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "https://open.bigmodel.cn/usercenter/apikeys"),
        new("Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k", "https://platform.moonshot.cn/console/api-keys"),
        new("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", "https://bailian.console.aliyun.com/?apiKey=1"),
        new("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct", "https://cloud.siliconflow.cn/account/ak"),
        new("Ollama(本机)", "http://localhost:11434/v1", "qwen2.5", "https://ollama.com/download"),
    ];

    public ServiceConfig MakeConfig(string id, bool isEnabled = true) => new()
    {
        Id = id, Kind = ProviderKind.OpenAICompatible, Name = Name, IsEnabled = isEnabled,
        Options = new() { ["baseURL"] = BaseUrl, ["model"] = Model },
    };
}
