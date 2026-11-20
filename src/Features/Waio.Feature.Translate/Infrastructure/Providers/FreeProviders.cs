using System.Text.Json;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure.Providers;

// 参考:MAIO Infrastructure/Providers/GoogleProvider.swift(其参考 pot-desktop src/services/translate/google)
/// <summary>Google 翻译(网页版 gtx 接口,免费、无需 Key)。查单词时返回音标和按词性分组的释义。</summary>
internal sealed class GoogleProvider(string baseUrl, Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var target = Code(request.Target) is { } t && request.Target != Language.Auto ? t : throw TranslationException.Unsupported(request.Target);
        var source = Code(request.Source) ?? "auto";
        var root = Http.UserProvided(baseUrl);
        var query = FormEncoding.Encode(
        [
            ("client", "gtx"), ("sl", source), ("tl", target), ("hl", target), ("dt", "t"), ("dt", "bd"), ("dt", "rm"),
            ("ie", "UTF-8"), ("oe", "UTF-8"), ("q", request.Text),
        ]);
        var url = new Uri($"{root.GetLeftPart(UriPartial.Authority)}/translate_a/single?{query}");
        var (data, status) = await http.DataWithStatusAsync(Http.Get(url), cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw Failure(status, data);
        }

        return Parse(data);
    }

    public const string RateLimitedMessage = "Google 限流了(当前网络出口 IP 请求过多),请换个代理节点或换个翻译源";

    /// <summary>
    /// 非 2xx 的错误。Google 按 IP 反滥用时返回 429(偶尔是 503)和一个 "Sorry..." 的 HTML 拦截页,
    /// 原样显示是一大段 HTML,这里换成能看懂的提示。
    /// </summary>
    public static TranslationException Failure(int status, byte[] data)
    {
        var body = Http.Snippet(data);
        var blockedPage = body.Contains("<title>Sorry", StringComparison.OrdinalIgnoreCase);
        return status == 429 || blockedPage
            ? new TranslationException(RateLimitedMessage)
            : TranslationException.Http(status, body);
    }

    public static TranslationResult Parse(byte[] data)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(data).RootElement;
        }
        catch (JsonException)
        {
            throw TranslationException.BadResponse(Http.Snippet(data));
        }

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
        {
            throw TranslationException.BadResponse(Http.Snippet(data));
        }

        var text = new System.Text.StringBuilder();
        var phonetics = new List<string>();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0)
            {
                continue;
            }

            if (segment[0].ValueKind == JsonValueKind.String)
            {
                text.Append(segment[0].GetString());
            }
            else if (segment.GetArrayLength() > 3 && segment[3].ValueKind == JsonValueKind.String && segment[3].GetString() is { Length: > 0 } phonetic)
            {
                phonetics.Add($"/{phonetic}/");
            }
        }

        var meanings = new List<DictionaryMeaning>();
        if (root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Array)
        {
            foreach (var group in root[1].EnumerateArray())
            {
                if (group.ValueKind == JsonValueKind.Array && group.GetArrayLength() > 1
                    && group[0].ValueKind == JsonValueKind.String && group[1].ValueKind == JsonValueKind.Array)
                {
                    meanings.Add(new DictionaryMeaning(group[0].GetString()!, [.. Json.Strings(group[1]).Take(8)]));
                }
            }
        }

        return new TranslationResult(text.ToString(), meanings.Count == 0 ? null : new DictionaryEntry(phonetics, meanings));
    }

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh-CN",
        Language.TraditionalChinese => "zh-TW",
        _ => language.Code(),
    };
}

// 参考:MAIO Infrastructure/Providers/TransmartProvider.swift(其参考 pot-desktop src/services/translate/transmart)
/// <summary>腾讯交互翻译(transmart.qq.com,免费、无需 Key、国内直连)。</summary>
internal sealed class TransmartProvider(Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var target = Code(request.Target) is { } t && t != "auto" ? t : throw TranslationException.Unsupported(request.Target);
        var body = new Dictionary<string, object>
        {
            ["header"] = new Dictionary<string, string> { ["fn"] = "auto_translation", ["client_key"] = "browser-chrome-130.0.0-Windows-waio" },
            ["type"] = "plain",
            ["model_category"] = "normal",
            ["source"] = new Dictionary<string, object> { ["lang"] = Code(request.Source) ?? "auto", ["text_list"] = new[] { request.Text } },
            ["target"] = new Dictionary<string, string> { ["lang"] = target },
        };
        var data = await http.DataAsync(Http.PostJson(new Uri("https://transmart.qq.com/api/imt"), body), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data);
        if (Json.Prop(root, "auto_translation") is not { ValueKind: JsonValueKind.Array } lines)
        {
            throw new TranslationException(Json.Str(root, "message") ?? "腾讯交互翻译没有返回结果");
        }

        return new TranslationResult(string.Join("\n", Json.Strings(lines)).Trim());
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
