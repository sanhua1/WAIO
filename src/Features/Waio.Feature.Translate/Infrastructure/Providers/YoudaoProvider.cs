using System.Globalization;
using System.Text;
using System.Text.Json;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure.Providers;

// 参考:MAIO YoudaoProvider.swift(其参考 pot-desktop src/services/translate/youdao)
/// <summary>有道智云·文本翻译(签名 v3)。查单词时附带音标和释义。</summary>
internal sealed class YoudaoProvider(string appKey, string appSecret, Http http) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, Action<string> onPartial, CancellationToken cancellationToken)
    {
        var target = request.TargetCode(Code);
        var source = request.SourceCode(Code);
        var salt = Guid.NewGuid().ToString().ToLowerInvariant();
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var fields = new[]
        {
            ("q", request.Text), ("from", source), ("to", target), ("appKey", appKey), ("salt", salt),
            ("sign", Sign(appKey, request.Text, salt, curtime, appSecret)), ("signType", "v3"), ("curtime", curtime),
        };
        var (data, status) = await http.DataWithStatusAsync(
            Http.PostForm(new Uri("https://openapi.youdao.com/api"), fields), cancellationToken).ConfigureAwait(false);
        var root = Json.Parse(data, status);
        if (Json.Str(root, "errorCode") is { } code && code != "0")
        {
            throw new TranslationException($"有道翻译错误 {code}:{Describe(code)}");
        }

        if (Json.Prop(root, "translation") is not { ValueKind: JsonValueKind.Array } lines)
        {
            throw TranslationException.BadResponse(Http.Snippet(data));
        }

        return new TranslationResult(string.Join("\n", Json.Strings(lines)), Dictionary(Json.Prop(root, "basic")));
    }

    private static DictionaryEntry? Dictionary(JsonElement? basic)
    {
        if (basic is not { } b)
        {
            return null;
        }

        var phonetics = new List<string>();
        if (Json.Str(b, "uk-phonetic") is { Length: > 0 } uk)
        {
            phonetics.Add($"英 /{uk}/");
        }

        if (Json.Str(b, "us-phonetic") is { Length: > 0 } us)
        {
            phonetics.Add($"美 /{us}/");
        }

        if (phonetics.Count == 0 && Json.Str(b, "phonetic") is { Length: > 0 } phonetic)
        {
            phonetics.Add($"/{phonetic}/");
        }

        var meanings = new List<DictionaryMeaning>();
        foreach (var line in Json.Strings(Json.Prop(b, "explains")))
        {
            // 形如 "n. 苹果;苹果树":第一个以 . 结尾的词是词性
            var head = line.Split(' ', 2);
            var hasPos = head.Length == 2 && head[0].EndsWith('.');
            var body = hasPos ? head[1] : line;
            meanings.Add(new DictionaryMeaning(hasPos ? head[0] : "", [.. body.Split('；').Select(t => t.Trim())]));
        }

        var entry = new DictionaryEntry(phonetics, meanings);
        return entry.IsEmpty ? null : entry;
    }

    /// <summary>v3 签名:sha256(应用ID + input + salt + curtime + 应用密钥)。</summary>
    public static string Sign(string appKey, string text, string salt, string curtime, string appSecret) =>
        Crypto.Sha256Hex(appKey + Truncate(text) + salt + curtime + appSecret);

    /// <summary>
    /// input 规则:长度 ≤ 20 时取原文,否则取「前 10 个字符 + 长度 + 后 10 个字符」。
    /// 按 Unicode 标量计数(与官方 Python 示例 len(q) 一致;MAIO 同样做法)。
    /// </summary>
    public static string Truncate(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= 20)
        {
            return text;
        }

        static string Join(IEnumerable<Rune> part) => string.Concat(part.Select(r => r.ToString()));
        return Join(runes.Take(10)) + runes.Length.ToString(CultureInfo.InvariantCulture) + Join(runes.Skip(runes.Length - 10));
    }

    private static string Describe(string code) => code switch
    {
        "101" => "缺少必填参数",
        "102" => "不支持的语言类型",
        "108" => "应用 ID 无效",
        "110" => "应用没有绑定文本翻译服务",
        "202" => "签名校验失败,请检查应用 ID 和应用密钥",
        "206" => "时间戳无效,请检查系统时间",
        "401" => "账户已欠费",
        "411" => "访问频率受限",
        _ => "请对照有道智云文档查看错误码含义",
    };

    public static string? Code(Language language) => language switch
    {
        Language.Auto => "auto",
        Language.SimplifiedChinese => "zh-CHS",
        Language.TraditionalChinese => "zh-CHT",
        _ => language.Code(),
    };
}
