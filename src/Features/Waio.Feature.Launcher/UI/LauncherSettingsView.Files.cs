using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Launcher.Index;
using Waio.Kit.Commands;
using Waio.UI;

namespace Waio.Feature.Launcher.UI;

/// <summary>
/// 设置页的"文件搜索"一节(P2,ADR-0024)。选项和默认值对应 Everything 的同名设置(都默认关闭 / 为空,和 Everything 一样)。
/// 索引状态从功能写的 index-status.json 读(设置页和运行中的功能只隔着这个文件)。
/// </summary>
internal sealed partial class LauncherSettingsView
{
    private static readonly (int, string)[] FileCounts = [(10, "10 个"), (20, "20 个"), (50, "50 个")];

    private static readonly (FileResultStyle, string)[] Styles =
        [(FileResultStyle.Compact, "极简(一行)"), (FileResultStyle.Detailed, "详细(两行,大图标)")];

    private UIElement FileSection()
    {
        var v = _value;
        var status = SettingsUi.Hint(StatusText());
        var refresh = SettingsUi.Button("刷新", () => status.Text = StatusText());
        var rebuild = SettingsUi.Button("重建索引", () => _ = RebuildAsync(status));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, rebuild } };

        var excluded = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 64,
            PlaceholderText = "每行一个完整路径,例如 D:\\备份",
            Text = string.Join(Environment.NewLine, v.ExcludeFolders),
        };
        var saveExcluded = SettingsUi.Button("保存", () => Save(s => s.ExcludeFolders = excluded.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 2 && p[1] == ':')
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()));

        return SettingsUi.Stack(
            SettingsUi.Section("文件搜索"),
            SettingsUi.Card(
                "全盘搜文件",
                SettingsUi.Lines("为整个磁盘建立文件索引并实时更新,几百万个文件也能打字即出结果", "需要以管理员身份运行"),
                SettingsUi.Toggle(v.FileSearchEnabled, x => Save(s => s.FileSearchEnabled = x))),
            SettingsUi.Card("索引状态", null, actions),
            status,
            SettingsUi.Card(
                "结果样式",
                SettingsUi.Lines("极简:每个文件一行(图标、名称、所在文件夹),一屏看到更多", "详细:和应用一样两行", "只影响文件和文件夹"),
                SettingsUi.Choice(Styles, v.FileResultStyle, x => Save(s => s.FileResultStyle = x))),
            SettingsUi.Card(
                "\"全部\"里最多显示几个文件",
                SettingsUi.Lines("想看更多按 Tab 切到\"文件\"(最多 300 个)", "前缀 f + 空格 只搜文件,d + 空格 只搜文件夹"),
                SettingsUi.Choice(FileCounts, Nearest(FileCounts, v.MaxFilesInAll), x => Save(s => s.MaxFilesInAll = x))),
            SettingsUi.Card(
                "没有匹配的应用时自动切到\"文件\"", "找到了文件才切;应用和文件都没有时留在\"全部\"(网页搜索还在)。点\"全部\"可以切回来",
                SettingsUi.Toggle(v.AutoSwitchToFiles, x => Save(s => s.AutoSwitchToFiles = x))),
            SettingsUi.Card(
                "索引可移动磁盘", "U 盘、移动硬盘(NTFS 格式)。默认只索引本机的固定磁盘",
                SettingsUi.Toggle(v.IncludeRemovableVolumes, x => Save(s => s.IncludeRemovableVolumes = x))),
            SettingsUi.Card(
                "不显示隐藏的文件和文件夹", "默认显示",
                SettingsUi.Toggle(v.ExcludeHiddenFiles, x => Save(s => s.ExcludeHiddenFiles = x))),
            SettingsUi.Card(
                "不显示系统文件和文件夹", "默认显示",
                SettingsUi.Toggle(v.ExcludeSystemFiles, x => Save(s => s.ExcludeSystemFiles = x))),
            SettingsUi.Card("排除的文件夹", "这些文件夹下面的内容不出现在结果里", saveExcluded),
            excluded);
    }

    private string StatusText()
    {
        if (!_value.FileSearchEnabled)
        {
            return "文件搜索已关闭";
        }

        if (IndexStatusReport.Read(_context.Storage.DataDirectory) is not { } report)
        {
            return "还没有索引状态(启动台没有运行,或者刚开始建立)";
        }

        var lines = report.Volumes.Select(v => v.State switch
        {
            nameof(VolumeState.Ready) =>
                $"{v.Root} {v.FileSystem}:{v.Entries:N0} 个文件和文件夹,约 {v.MemoryBytes >> 20:N0} MB 内存,扫描于 {v.ScannedAt?.LocalDateTime:yyyy-MM-dd HH:mm}",
            nameof(VolumeState.Scanning) => $"{v.Root}:正在建立索引 {v.Percent}%",
            nameof(VolumeState.Loading) or nameof(VolumeState.Waiting) => $"{v.Root}:正在读取索引…",
            _ => $"{v.Root}:{v.Message ?? v.State}",
        }).ToList();
        if (lines.Count == 0)
        {
            lines.Add("没有可以索引的磁盘(只支持 NTFS 格式)");
        }

        lines.Add($"(更新于 {report.UpdatedAt.LocalDateTime:HH:mm:ss})");
        return string.Join("\n", lines);
    }

    private async Task RebuildAsync(TextBlock status)
    {
        try
        {
            await _context.Commands.PerformAsync(LauncherFeature.RebuildIndex, CommandArguments.Empty);
            status.Text = "正在重建索引…(稍后点刷新查看进度)";
        }
        catch (CommandException ex)
        {
            _context.Logger.LogWarning(ex, "重建文件索引失败");
            status.Text = "启动台没有启用,无法重建索引";
        }
    }
}
