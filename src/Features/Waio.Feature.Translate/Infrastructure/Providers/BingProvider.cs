using System.Text.Json;
using System.Text.RegularExpressions;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure.Providers;

/// <summary>
/// 必应翻译(网页翻译器 bing.com/translator 的接口,免费、无需 Key、国内直连)。流程同 MAIO <c>BingProvider</c>:
/// 打开翻译器页面拿到 IG / IID / key / token 和 cookie,之后用它们调用 ttranslatev3。凭证缓存到过期为止。
/// (MAIO 注:Edge 的 edge.microsoft.com/translate/auth 接口 2026-11 实测已失效,不要再用。)
/// </summary>
internal sealed class BingProvider(BingSession session) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var target = Code(request.Target) is { } t && t != "auto-detect" ? t : throw TranslationException.Unsupported(request.Target);
        var source = Code(request.Source) ?? "auto-detect";
        return new TranslationResult(await session.TranslateAsync(request.Text, source, target, cancellationToken).ConfigureAwait(false));
    }

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto-detect",
        Language.SimplifiedChinese => "zh-Hans",
        Language.TraditionalChinese => "zh-Hant",
        _ => language.Code(),
    };
}

/// <summary>必应网页凭证与 cookie。整个功能共用一个(由 ProviderFactory 持有);并发安全。</summary>
internal sealed partial class BingSession : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    internal sealed record Credentials(string Host, string Ig, string Iid, string Key, string Token, DateTimeOffset ExpiresAt);

    // 独立的 cookie 容器:必应校验 cookie 与 token 是否来自同一会话
    private readonly HttpClient _client = Http.CreateClient(cookies: true);
    private readonly Http _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Credentials? _credentials;

    public BingSession()
    {
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http = new Http(_client);
    }

    /// <summary>提前获取凭证(第一次打开必应翻译页面约需 2~3 秒),让第一次翻译不用等。</summary>
    public async Task PrewarmAsync()
    {
        try
        {
            await CurrentAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TranslationException or OperationCanceledException)
        {
            // 预热失败无所谓,真正翻译时会再试
        }
    }

    public async Task<string> TranslateAsync(string text, string from, string to, CancellationToken cancellationToken)
    {
        try
        {
            return await AttemptAsync(text, from, to, await CurrentAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationException ex) when (ex.Data.Contains(BadResponseMark))
        {
            _credentials = null; // 凭证可能已失效:刷新后重试一次
            return await AttemptAsync(text, from, to, await CurrentAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
    }

    private const string BadResponseMark = "bing.badResponse";

    private async Task<string> AttemptAsync(string text, string from, string to, Credentials credentials, CancellationToken cancellationToken)
    {
        var url = new Uri($"https://{credentials.Host}/ttranslatev3?isVertical=1&IG={Uri.EscapeDataString(credentials.Ig)}&IID={Uri.EscapeDataString(credentials.Iid)}");
        var request = Http.PostForm(url, [("fromLang", from), ("to", to), ("text", text), ("token", credentials.Token), ("key", credentials.Key)]);
        var data = await _http.DataAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                && Json.Prop(root[0], "translations") is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0
                && Json.Str(list[0], "text") is { } translated)
            {
                return translated;
            }
        }
        catch (JsonException)
        {
        }

        var error = TranslationException.BadResponse(Http.Snippet(data));
        error.Data[BadResponseMark] = true;
        throw error;
    }

    private async Task<Credentials> CurrentAsync(CancellationToken cancellationToken)
    {
        if (_credentials is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credentials is { } again && again.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return again;
            }

            using var request = Http.Get(new Uri("https://www.bing.com/translator"));
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(Http.Timeout);
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, timer.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new TranslationException(Http.Describe(ex));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationException("请求超时,请检查网络或代理");
            }

            using (response)
            {
                var page = await response.Content.ReadAsStringAsync(timer.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw TranslationException.Http((int)response.StatusCode, page.Length > 300 ? page[..300] : page);
                }

                var host = response.RequestMessage?.RequestUri?.Host ?? "www.bing.com"; // 可能被重定向到 cn.bing.com
                _credentials = Parse(page, host, DateTimeOffset.UtcNow)
                    ?? throw new TranslationException("无法从必应翻译页面获取凭证(页面结构可能已变化)");
                return _credentials;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Credentials? Parse(string page, string host, DateTimeOffset now)
    {
        var ig = IgPattern().Match(page);
        var iid = IidPattern().Match(page);
        var helper = HelperPattern().Match(page);
        if (!ig.Success || !iid.Success || !helper.Success)
        {
            return null;
        }

        var parts = helper.Groups[1].Value.Split(',').Select(p => p.Trim('"', ' ')).ToArray();
        if (parts.Length < 2)
        {
            return null;
        }

        var lifetime = parts.Length > 2 && double.TryParse(parts[2], out var ms) ? ms / 1000 : 3600;
        // 提前 5 分钟过期,避免用到快失效的 token
        return new Credentials(host, ig.Groups[1].Value, iid.Groups[1].Value, parts[0], parts[1],
            now.AddSeconds(Math.Max(60, lifetime - 300)));
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    [GeneratedRegex("IG:\"([A-Za-z0-9]+)\"")]
    private static partial Regex IgPattern();

    [GeneratedRegex("data-iid=\"(translator\\.[0-9]+)\"")]
    private static partial Regex IidPattern();

    [GeneratedRegex(@"params_AbusePreventionHelper\s*=\s*\[([^\]]+)\]")]
    private static partial Regex HelperPattern();
}
