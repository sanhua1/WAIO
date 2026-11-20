using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Translate.Domain;
using Waio.Feature.Translate.Infrastructure;
using Waio.Kit.Commands;
using Waio.UI;

namespace Waio.Feature.Translate.Views;

/// <summary>
/// 翻译设置页(MAIO <c>TranslateSettingsView</c>,选项与说明文字一致):常规选项、翻译源列表(<c>.Services.cs</c>)、历史记录。
/// 每改一项立即保存;运行中的功能每次翻译都重新读设置,所以立即生效。
/// </summary>
internal sealed partial class TranslateSettingsView
{
    private static readonly (HotkeyBehavior, string)[] Behaviors =
    [
        (HotkeyBehavior.Smart, "有选中文字就翻译,否则打开输入框"),
        (HotkeyBehavior.InputOnly, "总是打开输入框"),
        (HotkeyBehavior.SelectionOnly, "只翻译选中的文字"),
    ];

    private static readonly (WindowPosition, string)[] Positions =
        [(WindowPosition.FollowMouse, "跟随鼠标"), (WindowPosition.Remember, "记住上次位置"), (WindowPosition.Center, "屏幕正中间")];

    private static readonly (SelectionPosition, string)[] SelectionPositions =
        [(SelectionPosition.Center, "屏幕正中间"), (SelectionPosition.Mouse, "鼠标处"), (SelectionPosition.NearSelection, "所选文字旁边")];

    private static readonly (Language, string)[] TargetChoices = [.. Languages.Targets.Select(l => (l, l.DisplayName()))];

    private readonly FeatureSettingsContext _context;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ProviderFactory _factory;
    private readonly HistoryStore _history;
    private TranslateSettings _value;

    private TranslateSettingsView(FeatureSettingsContext context)
    {
        _context = context;
        _value = context.Settings.Get<TranslateSettings>().Normalized();
        _factory = new ProviderFactory(context.Secrets); // "测试"按钮用;页面卸载时释放
        _history = HistoryStore.For(context.Storage.DataDirectory, context.Logger);
        _host.Unloaded += (_, _) => _factory.Dispose();
        Build();
    }

    public static UIElement Create(FeatureSettingsContext context) => new TranslateSettingsView(context)._host;

    private void Build()
    {
        var v = _value;
        var limitCard = SettingsUi.Card("最多保留", null,
            SettingsUi.Choice([.. TranslateSettings.HistoryLimits.Select(n => (n, $"{n} 条"))], v.HistoryLimit, x => Save(s => s.HistoryLimit = x)));
        limitCard.Visibility = Visible(v.HistoryEnabled);

        _host.Content = SettingsUi.Stack([
            SettingsUi.Section("常规"),
            SettingsUi.Card("按下快捷键时", Help.Behavior, SettingsUi.Choice(Behaviors, v.HotkeyBehavior, x => Save(s => s.HotkeyBehavior = x))),
            SettingsUi.Card("目标语言", Help.Target, SettingsUi.Choice(TargetChoices, v.TargetLanguage, x => Save(s => s.TargetLanguage = x))),
            SettingsUi.Card("原文已是目标语言时翻译成", Help.SecondTarget,
                SettingsUi.Choice(TargetChoices, v.SecondTargetLanguage, x => Save(s => s.SecondTargetLanguage = x))),
            SettingsUi.Card("自动删除换行", Help.LineBreaks, SettingsUi.Toggle(v.RemoveLineBreaks, x => Save(s => s.RemoveLineBreaks = x))),
            SettingsUi.Card("窗口位置", "直接打开输入框时窗口放在哪",
                SettingsUi.Choice(Positions, v.WindowPosition, x => Save(s => s.WindowPosition = x))),
            SettingsUi.Card("翻译选中文字时的窗口位置", Help.SelectionPosition,
                SettingsUi.Choice(SelectionPositions, v.SelectionWindowPosition, x => Save(s => s.SelectionWindowPosition = x))),
            SettingsUi.Card("失去焦点时关闭窗口", Help.CloseOnBlur, SettingsUi.Toggle(v.CloseOnBlur, x => Save(s => s.CloseOnBlur = x))),
            SettingsUi.Card("字体大小", null, FontSlider(v.FontSize)),
            .. ServiceSection(),
            SettingsUi.Section("翻译历史"),
            SettingsUi.Card("记录翻译历史", null, SettingsUi.Toggle(v.HistoryEnabled, x =>
            {
                limitCard.Visibility = Visible(x);
                Save(s => s.HistoryEnabled = x);
            })),
            limitCard,
            SettingsUi.Card("翻译历史", null, SettingsUi.Button("查看历史记录…", () => _ = HistoryDialog.ShowAsync(_history, _host.XamlRoot))),
            SettingsUi.Card("恢复默认设置", "保留翻译源和 Key", SettingsUi.Button("恢复默认", () =>
            {
                var services = _value.Services;
                _value = new TranslateSettings { Services = services };
                Save(_ => { });
                Build();
            })),
        ]);
    }

    private UIElement FontSlider(double value)
    {
        var label = new TextBlock { Text = $"{value:0} pt", Width = 44, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var slider = new Slider
        {
            Minimum = TranslateSettings.FontSizeRange.Min, Maximum = TranslateSettings.FontSizeRange.Max, StepFrequency = 1,
            Value = value, Width = 200, VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{e.NewValue:0} pt";
            Save(s => s.FontSize = e.NewValue);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(slider);
        row.Children.Add(label);
        return row;
    }

    /// <summary>用系统操作功能的 system.openUrl 打开网页(默认降权启动浏览器,ADR-0006)。</summary>
    private async Task OpenUrlAsync(string url)
    {
        try
        {
            var arguments = new CommandArguments(new Dictionary<string, JsonElement> { ["url"] = JsonSerializer.SerializeToElement(url) });
            await _context.Commands.PerformAsync("system.openUrl", arguments);
        }
        catch (CommandException ex)
        {
            _context.Logger.LogWarning(ex, "打开 {Url} 失败", url);
        }
    }

    /// <summary>改一项、立即保存。保存失败只记日志:界面上的值保留,下次改动会再尝试保存。</summary>
    private void Save(Action<TranslateSettings> change)
    {
        change(_value);
        _ = SaveAsync(_value);
    }

    private async Task SaveAsync(TranslateSettings value)
    {
        try
        {
            await _context.Settings.SaveAsync(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Logger.LogError(ex, "保存翻译设置失败");
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>设置项的说明文字(MAIO <c>TranslateHelp</c>,改成 Windows 的说法)。</summary>
    private static class Help
    {
        public const string SelectionPosition =
            "所选文字旁边:放在选中文字的下方,下方放不下时放上方。有些程序(例如部分浏览器、Electron 应用)不提供位置,这时放在鼠标处。上面的\"窗口位置\"只管直接打开输入框的情况。";

        public const string Behavior =
            "有选中文字就翻译,否则打开输入框:最常用,一个快捷键搞定两种场景。只翻译选中的文字:没有选中文字时只提示,不打开窗口。"
            + "读取选中文字时优先使用系统的辅助功能;读不到时(例如 Chrome、Electron 应用)会模拟一次 Ctrl+C,读完立即恢复剪贴板原来的内容,剪贴板历史也不会记录这次复制。";

        public const string Target = "原文会被翻译成这个语言。窗口里的语言栏可以临时改成其他语言。";

        public const string SecondTarget = "例如目标语言是中文时,选中中文会自动翻译成英文。原文语言在本机按文字识别,不联网。";

        public const string LineBreaks =
            "从 PDF、网页复制的文字常在句子中间断行,翻译质量会变差。打开后会把断开的行拼回完整段落,保留空行分隔的段落。窗口里也有手动删除换行的按钮。";

        public const string CloseOnBlur = "点击其他地方时自动关闭翻译窗口。点窗口右上角的图钉可以临时固定。";
    }
}
