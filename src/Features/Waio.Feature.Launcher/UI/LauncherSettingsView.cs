using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Launcher.Domain;
using Waio.UI;

namespace Waio.Feature.Launcher.UI;

/// <summary>
/// 启动台的设置页(ADR-0010;内容参考 MAIO 的 LauncherSettingsView 的"常规""网页搜索"两部分)。
/// 每改一项立即保存;运行中的功能通过设置的 Changed 事件实时生效。搜索引擎列表改完点"保存引擎"才生效。
/// </summary>
internal sealed partial class LauncherSettingsView
{
    private static readonly (double, string)[] KeepOptions =
        [(0, "每次清空"), (5, "5 秒"), (15, "15 秒"), (30, "30 秒"), (60, "1 分钟")];

    private static readonly (int, string)[] AppCounts = [(3, "3 个"), (5, "5 个"), (8, "8 个"), (12, "12 个")];

    private static readonly (int, string)[] Percents =
        [(30, "30%"), (35, "35%"), (40, "40%"), (45, "45%"), (50, "50%"), (60, "60%"), (70, "70%"), (80, "80%")];

    private readonly FeatureSettingsContext _context;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private LauncherSettings _value;

    private LauncherSettingsView(FeatureSettingsContext context)
    {
        _context = context;
        _value = context.Settings.Get<LauncherSettings>();
        Build();
    }

    public static UIElement Create(FeatureSettingsContext context) => new LauncherSettingsView(context)._host;

    private void Build()
    {
        var v = _value;
        var engines = v.Engines.Where(e => e.IsValid).Select(e => (e.Keyword.Trim(), $"{e.Name.Trim()}({e.Keyword.Trim()})")).ToList();
        var defaultEngine = v.FallbackEngine?.Keyword.Trim() ?? "";

        _host.Content = SettingsUi.Stack(
            SettingsUi.Section("面板"),
            SettingsUi.Card(
                "面板里的按键",
                "↑↓ 选择 · ⏎ 打开 · Ctrl+⏎ 打开文件位置 · Ctrl+C 复制路径 · Ctrl+1~9 打开第几项 · Tab 切换来源 · Esc 先清空再关闭。"
                + "鼠标:单击选中、双击打开,文件和文件夹可以直接拖到别的程序里"),
            SettingsUi.Card(
                "面板宽度", "占当前显示器的比例(下次打开生效)。屏幕很小或很大时会自动限制在合适的范围,保证列表和预览放得下",
                SettingsUi.Choice(Percents, Nearest(Percents, v.PanelWidthPercent), x => Save(s => s.PanelWidthPercent = x))),
            SettingsUi.Card(
                "面板高度", "占当前显示器的比例(下次打开生效)",
                SettingsUi.Choice(Percents, Nearest(Percents, v.PanelHeightPercent), x => Save(s => s.PanelHeightPercent = x))),
            SettingsUi.Card(
                "搜索方式",
                "应用名、拼音全拼、首字母都能搜:wx、weixin、微信 都能找到微信。前缀 a + 空格 只搜应用;g / bd / bi / gh + 空格 直接网页搜索"),
            SettingsUi.Card(
                "关闭后保留输入", "这段时间内再打开,还是上次的输入和选中项",
                SettingsUi.Choice(KeepOptions, Nearest(KeepOptions, v.KeepQuerySeconds), x => Save(s => s.KeepQuerySeconds = x))),
            SettingsUi.Card(
                "\"全部\"里最多显示几个应用", "想看更多按 Tab 切到\"应用\"(最多 50 个)",
                SettingsUi.Choice(AppCounts, Nearest(AppCounts, v.MaxAppsInAll), x => Save(s => s.MaxAppsInAll = x))),
            SettingsUi.Card(
                "输入为空时显示最近使用", "从启动台打开过的应用,常用的在前",
                SettingsUi.Toggle(v.ShowRecents, x => Save(s => s.ShowRecents = x))),
            SettingsUi.Card(
                "隐藏卸载程序", "开始菜单里\"卸载 xxx\"\"Uninstall xxx\"这类条目不出现在结果里",
                SettingsUi.Toggle(v.HideUninstallers, x => Save(s => s.HideUninstallers = x))),
            FileSection(),
            SettingsUi.Section("网页搜索"),
            SettingsUi.Card(
                "\"全部\"的最后一项是网页搜索", "没有想要的应用时,⏎ 直接用默认引擎搜索输入的内容",
                SettingsUi.Toggle(v.ShowWebFallback, x => Save(s => s.ShowWebFallback = x))),
            SettingsUi.Card(
                "默认引擎", null,
                engines.Count == 0
                    ? SettingsUi.Hint("没有可用的引擎")
                    : SettingsUi.Choice(engines, defaultEngine, x => Save(s => s.DefaultEngine = x))),
            EnginesEditor(),
            SettingsUi.Card(
                "恢复默认设置", "把以上所有选项(包括搜索引擎)恢复成默认值,不影响常用记录",
                SettingsUi.Button("恢复默认", () =>
                {
                    _value = new LauncherSettings();
                    Save(_ => { });
                    Build();
                })));
    }

    /// <summary>搜索引擎:每行"名称 | 关键词 | 网址",网址里用 {query} 表示输入的内容。</summary>
    private UIElement EnginesEditor()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            MinHeight = 120,
            Text = EngineListText.Format(_value.Engines),
        };
        var status = SettingsUi.Hint("");
        var save = SettingsUi.Button("保存引擎", () =>
        {
            var (engines, errors) = EngineListText.Parse(box.Text);
            if (errors.Count > 0)
            {
                status.Text = string.Join("\n", errors);
                return;
            }

            Save(s => s.Engines = engines);
            Build();
        });

        var card = SettingsUi.Card(
            "搜索引擎", "每行一个:名称 | 关键词 | 网址。网址里写 {query} 的地方换成输入的内容,例如 知乎 | zh | https://www.zhihu.com/search?q={query}",
            save);
        return SettingsUi.Stack(card, box, status);
    }

    private static T Nearest<T>((T Value, string Title)[] options, T value) =>
        options.Any(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ? value : options[1].Value;

    /// <summary>改一项、立即保存。保存失败只记日志:界面上的值保留,下次改动会再尝试保存。</summary>
    private void Save(Action<LauncherSettings> change)
    {
        change(_value);
        _ = SaveAsync(_value);
    }

    private async Task SaveAsync(LauncherSettings value)
    {
        try
        {
            await _context.Settings.SaveAsync(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Logger.LogError(ex, "保存启动台设置失败");
        }
    }
}
