using System.Text;
using System.Text.Json;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure.Providers;

// 参考:MAIO Infrastructure/Providers/OpenAICompatibleProvider.swift(其参考 pot-desktop src/services/translate/openai)
/// <summary>
/// 兼容 OpenAI Chat Completions 接口的大模型(DeepSeek、OpenAI、Gemini、智谱、Kimi、通义、硅基流动、Ollama…)。
/// 使用流式返回(SSE),译文逐字显示。
/// </summary>
internal sealed class OpenAICompatibleProvider(string baseUrl, string model, string? apiKey, PromptTemplate prompt, Http http)
    : ITranslationProvider
{
    /// <summary>大模型首字可能较慢,放宽超时(MAIO 60 秒)。</summary>
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(60);

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        if (model.Length == 0)
        {
            throw new TranslationException("还没有填写模型名称");
        }

        var (system, user) = prompt.Render(request);
        var headers = new Dictionary<string, string> { ["Accept"] = "text/event-stream" };
        if (!string.IsNullOrEmpty(apiKey))
        {
            headers["Authorization"] = $"Bearer {apiKey}";
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
            },
            ["stream"] = true,
        };
        using var message = Http.PostJson(Endpoint(baseUrl), body, headers);
        var text = new StringBuilder();
        var raw = new StringBuilder();
        await foreach (var line in http.LinesAsync(message, FirstByteTimeout, cancellationToken).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                raw.Append(line);
                continue;
            }

            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                break;
            }

            if (Content(payload) is { } piece)
            {
                text.Append(piece);
                onPartial(text.ToString());
            }
        }

        // 服务端没有按流式返回:按普通 JSON 解析
        var result = text.Length > 0 ? text.ToString() : NonStreaming(raw.ToString());
        return new TranslationResult(StripQuotes(result));
    }

    /// <summary>地址可以填到 /v1 为止,也可以填完整的 /chat/completions。</summary>
    public static Uri Endpoint(string baseUrl)
    {
        var url = Http.UserProvided(baseUrl);
        return url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? url
            : new Uri(url.ToString().TrimEnd('/') + "/chat/completions");
    }

    private static string? Content(string payload)
    {
        try
        {
            var root = JsonDocument.Parse(payload).RootElement;
            return Json.Prop(root, "choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0
                && Json.Prop(choices[0], "delta") is { } delta
                ? Json.Str(delta, "content")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NonStreaming(string raw)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            throw TranslationException.BadResponse(raw.Length > 300 ? raw[..300] : raw);
        }

        if (Json.Prop(root, "error") is { } error && Json.Str(error, "message") is { } message)
        {
            throw new TranslationException(message);
        }

        if (Json.Prop(root, "choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0
            && Json.Prop(choices[0], "message") is { } reply && Json.Str(reply, "content") is { } content)
        {
            return content;
        }

        throw TranslationException.BadResponse(raw.Length > 300 ? raw[..300] : raw);
    }

    /// <summary>有的模型会把译文包在提示词里的三引号中,去掉。</summary>
    public static string StripQuotes(string text)
    {
        var value = text.Trim();
        if (value.Length >= 6 && value.StartsWith("\"\"\"", StringComparison.Ordinal) && value.EndsWith("\"\"\"", StringComparison.Ordinal))
        {
            value = value[3..^3].Trim();
        }

        return value;
    }
}
