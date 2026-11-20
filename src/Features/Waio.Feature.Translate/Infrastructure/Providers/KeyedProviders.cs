using System.Text.Json;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure.Providers;

/// <summary>需要 API Key 的官方翻译接口(MAIO <c>KeyedProviders</c>)。<paramref name="values"/> 已包含该源全部配置项(含密钥,必填项已校验非空)。</summary>
internal static class KeyedProviders
{
    public static ITranslationProvider Make(ProviderKind kind, IReadOnlyDictionary<string, string> values, Http http)
    {
        string V(string key) => (values.GetValueOrDefault(key) ?? "").Trim();
        return kind switch
        {
            ProviderKind.DeepL => new DeepLProvider(V("authKey"), V("customURL"), http),
            ProviderKind.Baidu => new BaiduProvider(V("appID"), V("secret"), http),
            ProviderKind.Youdao => new YoudaoProvider(V("appKey"), V("appSecret"), http),
            ProviderKind.Tencent => new TencentProvider(V("secretID"), V("secretKey"), V("region"), http),
            ProviderKind.Alibaba => new AlibabaProvider(V("accessKeyID"), V("accessKeySecret"), http),
            ProviderKind.Volcengine => new VolcengineProvider(V("accessKeyID"), V("secretAccessKey"), http),
            ProviderKind.Caiyun => new CaiyunProvider(V("token"), http),
            ProviderKind.Niutrans => new NiutransProvider(V("apiKey"), http),
            _ => throw new TranslationException($"{kind.Spec().DisplayName} 不是需要 API Key 的翻译源"),
        };
    }
}

// 参考:MAIO DeepLProvider.swift(其参考 pot-desktop src/services/translate/deepl)
/// <summary>DeepL 官方 API(Key 以 :fx 结尾时用免费版地址),或用户自建的 DeepLX 地址。</summary>
internal sealed class DeepLProvider(string authKey, string customUrl, Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        if (customUrl.Length > 0)
        {
            return await DeepLxAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (authKey.Length == 0)
        {
            throw TranslationException.MissingSecret("Auth Key");
        }

        var body = new Dictionary<string, object> { ["text"] = new[] { request.Text }, ["target_lang"] = request.TargetCode(Code) };
        if (request.Source != Language.Auto)
        {
            body["source_lang"] = request.SourceCode(SourceCode);
        }

        var url = new Uri($"{Endpoint(authKey)}/v2/translate");
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostJson(url, body, new Dictionary<string, string> { ["Authorization"] = $"DeepL-Auth-Key {authKey}" }), cancellationToken).ConfigureAwait(false);
        if (status == 403)
        {
            throw new TranslationException("DeepL 认证失败(HTTP 403),请检查 Auth Key");
        }

        if (status == 456)
        {
            throw new TranslationException("DeepL 本月额度已用完(HTTP 456)");
        }

        var root = Json.Parse(data, status);
        if (Json.Prop(root, "translations") is { ValueKind: JsonValueKind.Array } items && status is >= 200 and < 300)
        {
            return new TranslationResult(string.Join("\n", items.EnumerateArray().Select(i => Json.Str(i, "text") ?? "")));
        }

        throw new TranslationException($"DeepL 错误(HTTP {status}):{Json.Str(root, "message") ?? Http.Snippet(data)}");
    }

    private async Task<TranslationResult> DeepLxAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>
        {
            ["text"] = request.Text,
            ["source_lang"] = request.Source == Language.Auto ? "auto" : request.SourceCode(SourceCode),
            ["target_lang"] = request.TargetCode(SourceCode),
        };
        var (data, status) = await http.DataWithStatusAsync(Http.PostJson(Http.UserProvided(customUrl), body), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        if (Json.Str(root, "data") is { } text && status is >= 200 and < 300)
        {
            return new TranslationResult(text);
        }

        throw new TranslationException($"DeepLX 错误(HTTP {status}):{Json.Str(root, "message") ?? Http.Snippet(data)}");
    }

    public static string Endpoint(string authKey) => authKey.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";

    /// <summary>目标语言代码(中文、英语、葡萄牙语需要区分变体)。</summary>
    public static string? Code(Language language) => language switch
    {
        Language.SimplifiedChinese => "ZH-HANS",
        Language.TraditionalChinese => "ZH-HANT",
        Language.English => "EN-US",
        Language.Portuguese => "PT-BR",
        _ => SourceCode(language),
    };

    /// <summary>源语言代码(不区分变体;Auto 时不传 source_lang)。</summary>
    public static string? SourceCode(Language language) => language switch
    {
        Language.Auto or Language.Vietnamese or Language.Thai or Language.Malay => null,
        Language.SimplifiedChinese or Language.TraditionalChinese => "ZH",
        _ => language.Code().ToUpperInvariant(),
    };
}

// 参考:MAIO BaiduProvider.swift(其参考 pot-desktop src/services/translate/baidu)
/// <summary>百度翻译开放平台·通用文本翻译。签名:md5(appid + q + salt + 密钥)。</summary>
internal sealed class BaiduProvider(string appId, string secret, Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var target = request.TargetCode(Code);
        var source = request.SourceCode(Code);
        var salt = SigningClock.Nonce();
        var fields = new[]
        {
            ("q", request.Text), ("from", source), ("to", target), ("appid", appId), ("salt", salt),
            ("sign", Sign(appId, request.Text, salt, secret)),
        };
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostForm(new Uri("https://fanyi-api.baidu.com/api/trans/vip/translate"), fields), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        if (Json.Str(root, "error_code") is { } code && code != "52000")
        {
            throw new TranslationException($"百度翻译错误 {code}:{Json.Str(root, "error_msg") ?? "未知错误"}");
        }

        if (Json.Prop(root, "trans_result") is not { ValueKind: JsonValueKind.Array } items)
        {
            throw TranslationException.BadResponse(Http.Snippet(data));
        }

        return new TranslationResult(string.Join("\n", items.EnumerateArray().Select(i => Json.Str(i, "dst") ?? "")));
    }

    public static string Sign(string appId, string text, string salt, string secret) => Crypto.Md5Hex(appId + text + salt + secret);

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "cht",
        Language.Japanese => "jp",
        Language.Korean => "kor",
        Language.French => "fra",
        Language.Spanish => "spa",
        Language.Vietnamese => "vie",
        Language.Arabic => "ara",
        Language.Malay => "may",
        Language.Ukrainian => "ukr",
        _ => language.Code(),
    };
}

// 参考:MAIO CaiyunProvider.swift(其参考 pot-desktop src/services/translate/caiyun;语言方向对照官方文档)
/// <summary>彩云小译 API。只支持部分语言方向:目标语言限中(简/繁)、英、日、韩。</summary>
internal sealed class CaiyunProvider(string token, Http http) : ITranslationProvider
{
    private static readonly HashSet<Language> Targets =
        [Language.SimplifiedChinese, Language.TraditionalChinese, Language.English, Language.Japanese, Language.Korean];

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        if (!Targets.Contains(request.Target))
        {
            throw TranslationException.Unsupported(request.Target);
        }

        var target = request.TargetCode(Code);
        var source = request.SourceCode(Code);
        var body = new Dictionary<string, object>
        {
            ["source"] = new[] { request.Text }, ["trans_type"] = $"{source}2{target}", ["detect"] = source == "auto",
            ["request_id"] = "waio", ["media"] = "text",
        };
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostJson(new Uri("https://api.interpreter.caiyunai.com/v1/translator"), body,
                new Dictionary<string, string> { ["X-Authorization"] = $"token {token}" }), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        if (Json.Prop(root, "target") is { ValueKind: JsonValueKind.Array } lines && status is >= 200 and < 300)
        {
            return new TranslationResult(string.Join("\n", Json.Strings(lines)));
        }

        var detail = Json.Str(root, "message") ?? Json.Str(root, "error") ?? Http.Snippet(data);
        throw new TranslationException($"彩云小译错误(HTTP {status}):{detail}");
    }

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "zh-Hant",
        Language.Thai or Language.Arabic or Language.Indonesian or Language.Malay or Language.Polish or Language.Dutch or Language.Ukrainian => null,
        _ => language.Code(),
    };
}

// 参考:MAIO NiutransProvider.swift(其参考 pot-desktop src/services/translate/niutrans)
/// <summary>小牛翻译开放平台·文本翻译(API Key 放在请求体里)。</summary>
internal sealed class NiutransProvider(string apiKey, Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>
        {
            ["from"] = request.SourceCode(Code), ["to"] = request.TargetCode(Code), ["apikey"] = apiKey, ["src_text"] = request.Text,
        };
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostJson(new Uri("https://api.niutrans.com/NiuTransServer/translation"), body), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        if (Json.Str(root, "error_code") is { } code)
        {
            throw new TranslationException($"小牛翻译错误 {code}:{Json.Str(root, "error_msg") ?? "未知错误"}");
        }

        return Json.Str(root, "tgt_text") is { } text
            ? new TranslationResult(text.Trim())
            : throw TranslationException.BadResponse(Http.Snippet(data));
    }

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "cht",
        _ => language.Code(),
    };
}
