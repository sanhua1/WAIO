namespace Waio.Feature.Launcher.Domain;

/// <summary>搜哪一类。顺序就是 Tab 切换的顺序。</summary>
internal enum LauncherSource
{
    All,
    Apps,
    Files,
    Folders,
}

internal static class LauncherSources
{
    /// <summary>Tab 在这些之间切换(同 MAIO;命令来源在 P4 加进来)。关掉文件搜索时没有"文件""文件夹"。</summary>
    public static IReadOnlyList<LauncherSource> For(LauncherSettings settings) => settings.FileSearchEnabled
        ? [LauncherSource.All, LauncherSource.Apps, LauncherSource.Files, LauncherSource.Folders]
        : [LauncherSource.All, LauncherSource.Apps];

    public static string Title(this LauncherSource source) => source switch
    {
        LauncherSource.Apps => "应用",
        LauncherSource.Files => "文件",
        LauncherSource.Folders => "文件夹",
        _ => "全部",
    };

    /// <summary>输入开头写这个前缀(后面跟空格)就只搜这一类。</summary>
    public static string? Prefix(this LauncherSource source) => source switch
    {
        LauncherSource.Apps => "a ",
        LauncherSource.Files => "f ",
        LauncherSource.Folders => "d ",
        _ => null,
    };

    /// <summary>这个来源要不要搜文件。</summary>
    public static bool IncludesFiles(this LauncherSource source) => source is LauncherSource.All or LauncherSource.Files or LauncherSource.Folders;
}

/// <summary>解析后的输入。</summary>
/// <param name="Source">实际搜索的来源(前缀优先于标签)。</param>
/// <param name="Text">去掉前缀后的文字。</param>
/// <param name="Engine">用了网页搜索引擎的关键词(<c>g 关键词</c>)。</param>
internal sealed record LauncherQuery(LauncherSource Source, string Text, WebSearchEngine? Engine)
{
    public bool IsEmpty => Text.Trim().Length == 0;

    /// <summary>要不要发起文件搜索。</summary>
    public bool WantsFiles => Engine is null && !IsEmpty && Source.IncludesFiles();

    public static LauncherQuery Parse(
        string raw, LauncherSource selected, IReadOnlyList<WebSearchEngine> engines, IReadOnlyList<LauncherSource>? available = null)
    {
        foreach (var source in available ?? [LauncherSource.All, LauncherSource.Apps])
        {
            if (source.Prefix() is { } prefix && raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new LauncherQuery(source, raw[prefix.Length..], null);
            }
        }

        var space = raw.IndexOf(' ');
        if (space > 0)
        {
            var keyword = raw[..space];
            foreach (var engine in engines)
            {
                if (engine.IsValid && string.Equals(engine.Keyword.Trim(), keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return new LauncherQuery(LauncherSource.All, raw[(space + 1)..], engine);
                }
            }
        }

        return new LauncherQuery(selected, raw, null);
    }
}

/// <summary>文件搜索的结果(异步到达)。</summary>
/// <param name="Query">是哪个输入的结果。</param>
/// <param name="Total">一共命中多少条(界面上写"共 N 个")。</param>
internal sealed record FileResults(string Query, IReadOnlyList<LauncherItem> Items, int Total)
{
    public static FileResults None { get; } = new("", [], 0);
}
