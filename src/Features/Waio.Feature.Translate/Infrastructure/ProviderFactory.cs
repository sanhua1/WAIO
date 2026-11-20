using Waio.Feature.Translate.Domain;
using Waio.Feature.Translate.Infrastructure.Providers;
using Waio.Kit.Services;

namespace Waio.Feature.Translate.Infrastructure;

/// <summary>
/// 根据配置创建翻译源(MAIO <c>ProviderFactory</c>)。持有需要跨次复用的状态:共用的 HttpClient、必应的网页凭证。
/// 线程安全:翻译窗口和设置页("测试"按钮)可以同时使用。
/// </summary>
internal sealed class ProviderFactory(ISecretStore secrets) : IDisposable
{
    private readonly HttpClient _client = Http.CreateClient();
    private readonly BingSession _bing = new();

    private Http HttpTool => new(_client);

    /// <summary>预热需要会话的翻译源(目前只有必应)。</summary>
    public void Prewarm(IEnumerable<ServiceConfig> services)
    {
        if (services.Any(s => s.Kind == ProviderKind.Bing && s.IsEnabled))
        {
            _ = _bing.PrewarmAsync();
        }
    }

    public ITranslationProvider Make(ServiceConfig config)
    {
        try
        {
            return Build(config);
        }
        catch (TranslationException ex)
        {
            return new FailingProvider(ex); // 配置不完整:翻译时直接在卡片上报告原因
        }
    }

    private ITranslationProvider Build(ServiceConfig config) => config.Kind switch
    {
        ProviderKind.Google => new GoogleProvider(config.Option("baseURL"), HttpTool),
        ProviderKind.Bing => new BingProvider(_bing),
        ProviderKind.Transmart => new TransmartProvider(HttpTool),
        ProviderKind.OpenAICompatible => new OpenAICompatibleProvider(
            config.Option("baseURL"), config.Option("model"), secrets.Get(config.SecretKey("apiKey")),
            new PromptTemplate(config.Option("systemPrompt"), config.Option("userPrompt")), HttpTool),
        _ => KeyedProviders.Make(config.Kind, ResolvedValues(config), HttpTool),
    };

    /// <summary>合并普通配置和密钥;必填项缺失时抛错(显示在结果卡片上)。</summary>
    private Dictionary<string, string> ResolvedValues(ServiceConfig config)
    {
        var values = new Dictionary<string, string>();
        foreach (var field in config.Spec.FieldList)
        {
            var value = field.Kind == FieldKind.Secret ? secrets.Get(config.SecretKey(field.Key)) ?? "" : config.Option(field.Key);
            if (field.IsRequired && value.Length == 0)
            {
                throw TranslationException.MissingSecret(field.Title);
            }

            values[field.Key] = value;
        }

        return values;
    }

    public void Dispose()
    {
        _client.Dispose();
        _bing.Dispose();
    }

    private sealed class FailingProvider(Exception error) : ITranslationProvider
    {
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken) =>
            Task.FromException<TranslationResult>(error);
    }
}
