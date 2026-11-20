using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure;

/// <summary>
/// 翻译源共用的网络请求工具(MAIO <c>HTTPClient</c>)。只做三件事:发请求、检查状态码、把错误变成 <see cref="TranslationException"/>。
/// HttpClient 默认使用系统代理(Clash 等不用单独设置)。
/// </summary>
internal sealed class Http(HttpClient client)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public HttpClient Client { get; } = client;

    public static HttpClient CreateClient(bool cookies = false) => new(new SocketsHttpHandler
    {
        UseCookies = cookies,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan, // 超时由每个请求自己的 CancellationToken 控制
    };

    /// <summary>发送请求,状态码不是 2xx 时抛出 Http(附带返回内容的前 300 个字符,方便看出原因)。</summary>
    public async Task<byte[]> DataAsync(HttpRequestMessage request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var (data, status) = await DataWithStatusAsync(request, cancellationToken, timeout).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw TranslationException.Http(status, Snippet(data));
        }

        return data;
    }

    /// <summary>发送请求但不检查状态码:有些官方接口在 4xx 时也会返回带错误信息的 JSON,由调用方自己解析。</summary>
    public async Task<(byte[] Data, int Status)> DataWithStatusAsync(
        HttpRequestMessage request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout ?? Timeout);
        try
        {
            using var response = await Client.SendAsync(request, timer.Token).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false);
            return (data, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationException("请求超时,请检查网络或代理");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(Describe(ex));
        }
    }

    /// <summary>解析 JSON;失败时抛 BadResponse(状态码不是 2xx 时抛 Http)。</summary>
    public static T DecodeJson<T>(byte[] data, int status = 200)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(data) ?? throw new JsonException("null");
        }
        catch (JsonException)
        {
            throw status is < 200 or >= 300 ? TranslationException.Http(status, Snippet(data)) : TranslationException.BadResponse(Snippet(data));
        }
    }

    /// <summary>逐行读取(大模型的 SSE 流式返回)。<paramref name="timeout"/> 只限制"等到响应头"的时间。</summary>
    public async IAsyncEnumerable<string> LinesAsync(
        HttpRequestMessage request, TimeSpan timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timer.CancelAfter(timeout);
            try
            {
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationException("请求超时,请检查网络或代理");
            }
            catch (HttpRequestException ex)
            {
                throw new TranslationException(Describe(ex));
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                throw TranslationException.Http((int)response.StatusCode, Snippet(body.Length > 2000 ? body[..2000] : body));
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return line;
            }
        }
    }

    public static string Snippet(byte[] data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            text = $"({data.Length} 字节,无法按 UTF-8 显示)";
        }

        return text.Length > 300 ? text[..300] : text;
    }

    /// <summary>网络错误的说明(MAIO <c>TranslateViewModel.describe</c> 对 URLError 的处理)。</summary>
    public static string Describe(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => "无法连接到服务器(可能需要代理)",
        HttpRequestError.ProxyTunnelError => "代理连接失败",
        HttpRequestError.SecureConnectionError => "安全连接失败(证书或代理问题)",
        _ => ex.Message,
    };

    public static HttpRequestMessage Get(Uri url, IReadOnlyDictionary<string, string>? headers = null) =>
        WithHeaders(new HttpRequestMessage(HttpMethod.Get, url), headers);

    /// <summary>JSON 请求体。</summary>
    public static HttpRequestMessage PostJson(Uri url, object body, IReadOnlyDictionary<string, string>? headers = null) =>
        PostBytes(url, JsonSerializer.SerializeToUtf8Bytes(body), "application/json", headers);

    public static HttpRequestMessage PostBytes(Uri url, byte[] body, string contentType, IReadOnlyDictionary<string, string>? headers = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return WithHeaders(new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, headers);
    }

    /// <summary>表单请求体(application/x-www-form-urlencoded,按 RFC 3986 编码)。</summary>
    public static HttpRequestMessage PostForm(Uri url, IEnumerable<(string, string)> fields, IReadOnlyDictionary<string, string>? headers = null) =>
        PostBytes(url, Encoding.UTF8.GetBytes(FormEncoding.Encode(fields)), "application/x-www-form-urlencoded", headers);

    private static HttpRequestMessage WithHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    /// <summary>由用户填写的地址构造 URL:补全 https://,去掉末尾的 /。</summary>
    public static Uri UserProvided(string text)
    {
        var value = text.Trim().TrimEnd('/');
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Host.Length > 0
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : throw new TranslationException($"地址格式不正确:{text}");
    }
}

internal static class FormEncoding
{
    /// <summary>按 RFC 3986 编码(空格编码为 %20,签名算法通常要求这种形式)。</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value);

    public static string Encode(IEnumerable<(string Key, string Value)> fields) =>
        string.Join("&", fields.Select(f => $"{Escape(f.Key)}={Escape(f.Value)}"));
}

/// <summary>各翻译源签名算法用到的摘要函数,全部输出小写十六进制(MAIO <c>Crypto</c>)。</summary>
internal static class Crypto
{
    public static string Md5Hex(string text) => Hex(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256Hex(string text) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256Hex(byte[] data) => Hex(SHA256.HashData(data));

    public static byte[] HmacSha256(string message, byte[] key) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));

    public static string HmacSha256Hex(string message, byte[] key) => Hex(HmacSha256(message, key));

    public static string HmacSha1Base64(string message, string key) =>
        Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>签名用的随机数与时间格式(MAIO <c>SigningClock</c>)。</summary>
internal static class SigningClock
{
    public static string Nonce() => Random.Shared.NextInt64(100_000, uint.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>2019-02-25</summary>
    public static string UtcDay(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>2016-02-23T12:46:24Z</summary>
    public static string Iso8601(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>20201230T081805Z</summary>
    public static string CompactIso8601(DateTimeOffset date) => date.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>有的接口把错误码写成数字,有的写成字符串,统一读成字符串。</summary>
internal static class Json
{
    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static IEnumerable<string> Strings(JsonElement? array) =>
        array is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    /// <summary>解析 JSON 文档;失败抛 BadResponse / Http。</summary>
    public static JsonElement Parse(byte[] data, int status = 200)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw status is < 200 or >= 300 ? TranslationException.Http(status, Http.Snippet(data)) : TranslationException.BadResponse(Http.Snippet(data));
        }
    }
}
