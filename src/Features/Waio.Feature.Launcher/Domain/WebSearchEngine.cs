using System.Text.Json.Serialization;

namespace Waio.Feature.Launcher.Domain;

/// <summary>
/// 网页搜索引擎:<c>关键词 + 空格 + 内容</c> 直接用它搜;URL 模板里的 <c>{query}</c> 换成 URL 编码后的内容。
/// 设置文件里的可变 POCO(System.Text.Json 需要可写属性)。
/// </summary>
internal sealed class WebSearchEngine
{
    public const string Placeholder = "{query}";

    public string Name { get; set; } = "";

    public string Keyword { get; set; } = "";

    public string Template { get; set; } = "";

    public static List<WebSearchEngine> Defaults() =>
    [
        new() { Name = "Google", Keyword = "g", Template = "https://www.google.com/search?q={query}" },
        new() { Name = "百度", Keyword = "bd", Template = "https://www.baidu.com/s?wd={query}" },
        new() { Name = "必应", Keyword = "bi", Template = "https://www.bing.com/search?q={query}" },
        new() { Name = "GitHub", Keyword = "gh", Template = "https://github.com/search?q={query}" },
    ];

    /// <summary>可用的引擎:有名字、关键词不含空格、模板是 http(s) 且含 {query}。</summary>
    [JsonIgnore]
    public bool IsValid =>
        Name.Trim().Length > 0
        && Keyword.Trim().Length > 0
        && !Keyword.Trim().Contains(' ')
        && Template.Contains(Placeholder, StringComparison.Ordinal)
        && (Template.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || Template.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>搜索网址;内容为空或模板无效时返回 null。</summary>
    public string? UrlFor(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || !IsValid)
        {
            return null;
        }

        return Template.Replace(Placeholder, Uri.EscapeDataString(trimmed), StringComparison.Ordinal);
    }
}
