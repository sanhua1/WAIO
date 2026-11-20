namespace Waio.Feature.Launcher.Domain;

internal enum LauncherItemKind
{
    App,
    Web,
    File,
    Folder,

    /// <summary>"全部"里文件组末尾的"查看全部 N 个":不是真实的目标,打开 = 切到"文件"标签。</summary>
    MoreFiles,
}

/// <summary>开始菜单"所有应用"里的一项(<see cref="Infrastructure.AppCatalog"/> 扫描得到)。</summary>
/// <param name="ParsingName">在 <c>shell:AppsFolder</c> 下的名字:商店应用是 AppUserModelID,传统程序常是"已知文件夹 GUID\相对路径"。</param>
/// <param name="DisplayName">系统给的显示名(跟随系统语言,"计算器")。</param>
/// <param name="TargetPath">快捷方式指向的程序(传统程序才有),用于"打开文件位置"和复制路径。</param>
internal sealed record AppEntry(string ParsingName, string DisplayName, string? TargetPath)
{
    /// <summary>商店 / 打包应用:AppUserModelID 形如 <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>。</summary>
    public bool IsPackaged => ParsingName.Contains('!') && TargetPath is null;
}

/// <summary>结果列表里的一项。</summary>
/// <param name="Id">常用度记录的键,同一个应用每次扫描都一样。</param>
/// <param name="Target">打开时交给 <c>ILauncher</c> 的目标:应用是 <c>shell:AppsFolder\…</c>,网页是网址。</param>
/// <param name="Path">磁盘上的文件(能"打开文件位置"、复制路径);没有为 null。</param>
internal sealed record LauncherItem(
    string Id, LauncherItemKind Kind, string Title, string Subtitle, string Target, string? Path, SearchKeys Keys)
{
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>结果右侧的类别文字。</summary>
    public string KindLabel => Kind switch
    {
        LauncherItemKind.Web => "网页",
        LauncherItemKind.File => "文件",
        LauncherItemKind.Folder => "文件夹",
        LauncherItemKind.MoreFiles => "",
        _ => "应用",
    };

    /// <summary>应用的 id 只和它在 AppsFolder 里的名字有关(不分大小写)。</summary>
    public static string AppId(string parsingName) => "app:" + parsingName.ToLowerInvariant();

    /// <summary>
    /// 应用。可搜索的名称:显示名、程序文件名(<c>WINWORD</c>、<c>notepad</c>)、
    /// 商店应用的包名去掉发布者前缀(<c>WindowsCalculator</c> → 能用 calc 搜到计算器)。
    /// </summary>
    public static LauncherItem App(AppEntry entry)
    {
        var exe = entry.TargetPath is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) : null;
        var subtitle = entry.TargetPath ?? (entry.IsPackaged ? "Microsoft Store 应用" : "");
        return new LauncherItem(
            AppId(entry.ParsingName), LauncherItemKind.App, entry.DisplayName, subtitle,
            AppsFolderPrefix + entry.ParsingName, entry.TargetPath,
            SearchKeys.From([entry.DisplayName, exe, PackageAppName(entry)]));
    }

    /// <summary>文件或文件夹(全盘索引的结果)。副标题是所在的文件夹;id 按路径(不分大小写),常用度跟着路径走。</summary>
    public static LauncherItem File(string path, string name, bool isDirectory)
    {
        var parent = System.IO.Path.GetDirectoryName(path) ?? "";
        return new LauncherItem(
            "file:" + path.ToLowerInvariant(), isDirectory ? LauncherItemKind.Folder : LauncherItemKind.File,
            name, parent, path, path, SearchKeys.Empty);
    }

    /// <summary>
    /// "查看全部 N 个"("全部"只列 <see cref="LauncherSettings.MaxFilesInAll"/> 个,其余的要能看到)。
    /// <paramref name="sourceLimit"/> 是"文件"标签最多列几个;总数超过它时如实写"前 300 个"。
    /// </summary>
    public static LauncherItem MoreFiles(int total, int sourceLimit) => new(
        "more:files", LauncherItemKind.MoreFiles,
        total <= sourceLimit ? $"查看全部 {total:N0} 个" : $"查看前 {sourceLimit:N0} 个(共 {total:N0} 个)",
        "切到“文件”标签(也可以按 Tab)", "", null, SearchKeys.Empty);

    /// <summary>有没有系统图标可取(应用、文件、文件夹;网页搜索和"查看全部"用字形图标)。</summary>
    public bool HasShellIcon => Kind is LauncherItemKind.App or LauncherItemKind.File or LauncherItemKind.Folder;

    /// <summary>网页搜索的那一项;引擎或内容无效时返回 null。</summary>
    public static LauncherItem? Web(WebSearchEngine engine, string text)
    {
        if (engine.UrlFor(text) is not { } url)
        {
            return null;
        }

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        return new LauncherItem(
            "web:" + engine.Keyword.Trim().ToLowerInvariant(), LauncherItemKind.Web,
            $"用 {engine.Name.Trim()} 搜索“{text.Trim()}”", host, url, null, SearchKeys.Empty);
    }

    /// <summary><c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c> → <c>WindowsCalculator</c>。</summary>
    internal static string? PackageAppName(AppEntry entry)
    {
        if (!entry.IsPackaged)
        {
            return null;
        }

        var family = entry.ParsingName[..entry.ParsingName.IndexOf('!')];
        var underscore = family.IndexOf('_');
        var name = underscore > 0 ? family[..underscore] : family;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}

/// <summary>一组结果("全部"里按类别分组)。</summary>
internal sealed record LauncherSection(string Title, IReadOnlyList<LauncherItem> Items);
