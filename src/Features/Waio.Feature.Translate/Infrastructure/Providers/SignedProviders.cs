using System.Globalization;
using System.Text;
using System.Text.Json;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure.Providers;

// 参考:MAIO TencentProvider.swift(其参考 pot-desktop src/services/translate/tencent;签名对照官方文档 551/30636)
/// <summary>腾讯云机器翻译 TextTranslate(API 3.0,签名 TC3-HMAC-SHA256)。</summary>
internal sealed class TencentProvider(string secretId, string secretKey, string region, Http http) : ITranslationProvider
{
    public const string Host = "tmt.tencentcloudapi.com";
    public const string ContentType = "application/json; charset=utf-8";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["SourceText"] = request.Text, ["Source"] = request.SourceCode(Code), ["Target"] = request.TargetCode(Code), ["ProjectId"] = 0,
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signing = new TencentSigning(Host, "tmt", "TextTranslate", ContentType, payload, timestamp);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = signing.Authorization(secretId, secretKey), ["X-TC-Action"] = "TextTranslate",
            ["X-TC-Version"] = "2018-03-21", ["X-TC-Timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["X-TC-Region"] = region.Length == 0 ? "ap-beijing" : region,
        };
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostBytes(new Uri($"https://{Host}"), payload, ContentType, headers), cancellationToken).ConfigureAwait(false);
        var response = Json.Prop(Json.Parse(data, status), "Response") ?? throw TranslationException.BadResponse(Http.Snippet(data));
        if (Json.Prop(response, "Error") is { } error)
        {
            throw new TranslationException($"腾讯云翻译错误 {Json.Str(error, "Code")}:{Json.Str(error, "Message")}");
        }

        return Json.Str(response, "TargetText") is { } text ? new TranslationResult(text) : throw TranslationException.BadResponse(Http.Snippet(data));
    }

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "zh-TW",
        Language.Polish or Language.Dutch or Language.Ukrainian => null,
        _ => language.Code(),
    };
}

/// <summary>TC3-HMAC-SHA256 签名。签名头:content-type;host;x-tc-action(与官方文档示例一致)。</summary>
internal sealed record TencentSigning(string Host, string Service, string Action, string ContentType, byte[] Payload, long Timestamp)
{
    public const string Algorithm = "TC3-HMAC-SHA256";
    public const string SignedHeaders = "content-type;host;x-tc-action";

    public string Date => SigningClock.UtcDay(DateTimeOffset.FromUnixTimeSeconds(Timestamp));

    public string CredentialScope => $"{Date}/{Service}/tc3_request";

    public string CanonicalRequest => string.Join("\n",
        "POST", "/", "", $"content-type:{ContentType}\nhost:{Host}\nx-tc-action:{Action.ToLowerInvariant()}\n",
        SignedHeaders, Crypto.Sha256Hex(Payload));

    public string StringToSign => string.Join("\n",
        Algorithm, Timestamp.ToString(CultureInfo.InvariantCulture), CredentialScope, Crypto.Sha256Hex(CanonicalRequest));

    public string Signature(string secretKey)
    {
        var secretDate = Crypto.HmacSha256(Date, Encoding.UTF8.GetBytes("TC3" + secretKey));
        var secretService = Crypto.HmacSha256(Service, secretDate);
        var secretSigning = Crypto.HmacSha256("tc3_request", secretService);
        return Crypto.HmacSha256Hex(StringToSign, secretSigning);
    }

    public string Authorization(string secretId, string secretKey) =>
        $"{Algorithm} Credential={secretId}/{CredentialScope}, SignedHeaders={SignedHeaders}, Signature={Signature(secretKey)}";
}

// 参考:MAIO AlibabaProvider.swift(其参考 pot-desktop src/services/translate/alibaba;签名对照阿里云"RPC 风格请求签名"V1)
/// <summary>阿里云机器翻译·通用版 TranslateGeneral(RPC 风格,签名 HMAC-SHA1 v1.0)。</summary>
internal sealed class AlibabaProvider(string accessKeyId, string accessKeySecret, Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var parameters = new List<(string, string)>
        {
            ("AccessKeyId", accessKeyId), ("Action", "TranslateGeneral"), ("Format", "JSON"), ("FormatType", "text"),
            ("Scene", "general"), ("SignatureMethod", "HMAC-SHA1"), ("SignatureNonce", Guid.NewGuid().ToString().ToUpperInvariant()),
            ("SignatureVersion", "1.0"), ("SourceLanguage", request.SourceCode(Code)), ("SourceText", request.Text),
            ("TargetLanguage", request.TargetCode(Code)), ("Timestamp", SigningClock.Iso8601(DateTimeOffset.UtcNow)), ("Version", "2018-10-12"),
        };
        var fields = parameters.Append(("Signature", Signature("POST", parameters, accessKeySecret)));
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostForm(new Uri("https://mt.cn-hangzhou.aliyuncs.com/"), fields), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        var code = Json.Str(root, "Code");
        if (code == "200" && Json.Prop(root, "Data") is { } payload && Json.Str(payload, "Translated") is { } text)
        {
            return new TranslationResult(text);
        }

        throw new TranslationException($"阿里翻译错误 {code ?? $"HTTP {status}"}:{Json.Str(root, "Message") ?? Http.Snippet(data)}");
    }

    /// <summary>待签名串:<c>方法&amp;%2F&amp;编码(按参数名排序后的 k=v&amp;k=v)</c>,密钥为 <c>AccessKeySecret&amp;</c>。</summary>
    public static string StringToSign(string method, IEnumerable<(string Key, string Value)> parameters)
    {
        var canonical = string.Join("&", parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{FormEncoding.Escape(p.Key)}={FormEncoding.Escape(p.Value)}"));
        return $"{method}&{FormEncoding.Escape("/")}&{FormEncoding.Escape(canonical)}";
    }

    public static string Signature(string method, IEnumerable<(string, string)> parameters, string secret) =>
        Crypto.HmacSha1Base64(StringToSign(method, parameters), secret + "&");

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "zh-tw",
        _ => language.Code(),
    };
}

// 参考:MAIO VolcengineProvider.swift(其参考 pot-desktop src/services/translate/volcengine;签名对照官方文档 6369/67269)
/// <summary>火山引擎机器翻译 TranslateText(OpenAPI 签名 HMAC-SHA256,与 AWS SigV4 同构)。</summary>
internal sealed class VolcengineProvider(string accessKeyId, string secretAccessKey, Http http) : ITranslationProvider
{
    public const string Host = "translate.volcengineapi.com";
    public const string Query = "Action=TranslateText&Version=2020-06-01";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["TargetLanguage"] = request.TargetCode(Code), ["TextList"] = new[] { request.Text } };
        if (request.Source != Language.Auto)
        {
            body["SourceLanguage"] = request.SourceCode(Code);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        var signing = new VolcengineSigning(Host, Query, payload, DateTimeOffset.UtcNow);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = signing.Authorization(accessKeyId, secretAccessKey), ["X-Date"] = signing.Timestamp,
            ["X-Content-Sha256"] = Crypto.Sha256Hex(payload),
        };
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostBytes(new Uri($"https://{Host}/?{Query}"), payload, VolcengineSigning.ContentType, headers), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        if (Json.Prop(root, "ResponseMetadata") is { } meta && Json.Prop(meta, "Error") is { } error)
        {
            throw new TranslationException($"火山翻译错误 {Json.Str(error, "Code")}:{Json.Str(error, "Message") ?? "未知错误"}");
        }

        return Json.Prop(root, "TranslationList") is { ValueKind: JsonValueKind.Array } items
            ? new TranslationResult(string.Join("\n", items.EnumerateArray().Select(i => Json.Str(i, "Translation") ?? "")))
            : throw TranslationException.BadResponse(Http.Snippet(data));
    }

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh",
        Language.TraditionalChinese => "zh-Hant",
        _ => language.Code(),
    };
}

/// <summary>火山引擎 OpenAPI 签名。签名头:content-type;host;x-content-sha256;x-date。</summary>
internal sealed record VolcengineSigning(string Host, string Query, byte[] Payload, DateTimeOffset Date)
{
    public const string ContentType = "application/json";
    public const string SignedHeaders = "content-type;host;x-content-sha256;x-date";

    public string Region { get; init; } = "cn-north-1";

    public string Service { get; init; } = "translate";

    /// <summary>X-Date,例如 20201230T081805Z。</summary>
    public string Timestamp => SigningClock.CompactIso8601(Date);

    public string CredentialScope => $"{Timestamp[..8]}/{Region}/{Service}/request";

    public string CanonicalRequest
    {
        get
        {
            var bodyHash = Crypto.Sha256Hex(Payload);
            var headers = $"content-type:{ContentType}\nhost:{Host}\nx-content-sha256:{bodyHash}\nx-date:{Timestamp}\n";
            return string.Join("\n", "POST", "/", Query, headers, SignedHeaders, bodyHash);
        }
    }

    public string StringToSign => string.Join("\n", "HMAC-SHA256", Timestamp, CredentialScope, Crypto.Sha256Hex(CanonicalRequest));

    public string Signature(string secret)
    {
        var kDate = Crypto.HmacSha256(Timestamp[..8], Encoding.UTF8.GetBytes(secret));
        var kRegion = Crypto.HmacSha256(Region, kDate);
        var kService = Crypto.HmacSha256(Service, kRegion);
        var kSigning = Crypto.HmacSha256("request", kService);
        return Crypto.HmacSha256Hex(StringToSign, kSigning);
    }

    public string Authorization(string accessKeyId, string secret) =>
        $"HMAC-SHA256 Credential={accessKeyId}/{CredentialScope}, SignedHeaders={SignedHeaders}, Signature={Signature(secret)}";
}
