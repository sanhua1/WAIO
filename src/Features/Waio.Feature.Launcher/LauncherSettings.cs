using System.Text.Json.Serialization;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher;

/// <summary>
/// 启动台设置(features/launcher.json)。字段只增不删;默认值与 MAIO 的 LauncherSettings 对齐,
/// <see cref="HideUninstallers"/> 是 Windows 特有的(开始菜单里常有"卸载 xxx"的快捷方式)。
/// </summary>
internal sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>关闭后这么多秒内再打开,保留上次的输入(0 = 每次都清空)。</summary>
    public double KeepQuerySeconds { get; set; } = 5;

    /// <summary>"全部"里最多显示几个应用。</summary>
    public int MaxAppsInAll { get; set; } = 5;

    /// <summary>输入为空时显示最近使用。</summary>
    public bool ShowRecents { get; set; } = true;

    /// <summary>"全部"的最后一项是"用默认引擎搜索…"。</summary>
    public bool ShowWebFallback { get; set; } = true;

    /// <summary>默认搜索引擎的关键词。</summary>
    public string DefaultEngine { get; set; } = "g";

    public List<WebSearchEngine> Engines { get; set; } = WebSearchEngine.Defaults();

    /// <summary>面板宽占当前显示器工作区的百分比(还受逻辑像素上下限约束,见 PanelGeometry)。</summary>
    public int PanelWidthPercent { get; set; } = Panel.PanelGeometry.DefaultWidthPercent;

    /// <summary>面板高占当前显示器工作区的百分比。</summary>
    public int PanelHeightPercent { get; set; } = Panel.PanelGeometry.DefaultHeightPercent;

    /// <summary>不显示名字里带"卸载 / Uninstall"的条目。</summary>
    public bool HideUninstallers { get; set; } = true;

    // ---- 文件搜索(P2,ADR-0024)。索引范围的默认值和 Everything 的默认设置相同(依据其公开的帮助文档) ----

    /// <summary>文件搜索(全盘索引)。</summary>
    public bool FileSearchEnabled { get; set; } = true;

    /// <summary>"全部"里最多显示几个文件("文件""文件夹"来源最多 300;同 MAIO)。</summary>
    public int MaxFilesInAll { get; set; } = 20;

    /// <summary>"全部"里没有匹配的应用、但找到了文件时,自动切到"文件"(默认开)。</summary>
    public bool AutoSwitchToFiles { get; set; } = true;

    /// <summary>文件、文件夹结果的样式:<c>compact</c> 一行(小图标 + 名称 + 所在文件夹,同 Everything,省高度)/ <c>detailed</c> 两行大图标。</summary>
    public FileResultStyle FileResultStyle { get; set; } = FileResultStyle.Compact;

    /// <summary>也索引可移动的 NTFS 卷(U 盘、移动硬盘)。Everything 的 auto_include_removable_volumes 默认 0。</summary>
    public bool IncludeRemovableVolumes { get; set; }

    /// <summary>不显示带"隐藏"属性的文件和文件夹。Everything 的 exclude_hidden_files_and_folders 默认 0。</summary>
    public bool ExcludeHiddenFiles { get; set; }

    /// <summary>不显示带"系统"属性的文件和文件夹。Everything 的 exclude_system_files_and_folders 默认 0。</summary>
    public bool ExcludeSystemFiles { get; set; }

    /// <summary>不显示这些文件夹下面的内容(完整路径)。Everything 的 exclude_folders 默认为空。</summary>
    public List<string> ExcludeFolders { get; set; } = [];

    [JsonIgnore]
    public WebSearchEngine? FallbackEngine =>
        Engines.FirstOrDefault(e => e.IsValid && string.Equals(e.Keyword.Trim(), DefaultEngine.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Engines.FirstOrDefault(e => e.IsValid);
}

/// <summary>文件搜索结果的样式。</summary>
internal enum FileResultStyle
{
    /// <summary>一行:小图标 + 名称 + 所在文件夹(同 Everything,一屏能看到更多结果)。</summary>
    Compact,

    /// <summary>两行:大图标、名称,下面是所在文件夹(和应用一样)。</summary>
    Detailed,
}
