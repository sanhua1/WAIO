namespace Waio.Feature.Launcher.Domain;

/// <summary>设置页里搜索引擎列表的文字形式:每行"名称 | 关键词 | 网址"。</summary>
internal static class EngineListText
{
    public static string Format(IEnumerable<WebSearchEngine> engines) =>
        string.Join(Environment.NewLine, engines.Select(e => $"{e.Name} | {e.Keyword} | {e.Template}"));

    /// <summary>解析;有错误时 <c>Errors</c> 非空(逐行说明),调用方不应保存。</summary>
    public static (List<WebSearchEngine> Engines, List<string> Errors) Parse(string text)
    {
        var engines = new List<WebSearchEngine>();
        var errors = new List<string>();
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            var engine = parts.Length == 3 ? new WebSearchEngine { Name = parts[0], Keyword = parts[1], Template = parts[2] } : null;
            if (engine is null || !engine.IsValid)
            {
                errors.Add($"第 {i + 1} 行写得不对:要\"名称 | 关键词 | 网址\",关键词不能有空格,网址以 http(s):// 开头并包含 {{query}}");
            }
            else if (!keywords.Add(engine.Keyword))
            {
                errors.Add($"第 {i + 1} 行:关键词 {engine.Keyword} 重复了");
            }
            else
            {
                engines.Add(engine);
            }
        }

        if (engines.Count == 0 && errors.Count == 0)
        {
            errors.Add("至少要有一个引擎");
        }

        return (engines, errors);
    }
}
