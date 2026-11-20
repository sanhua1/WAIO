using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Commands;
using Waio.UI;

namespace Waio.Feature.Clipboard.Views;

/// <summary>
/// 剪贴板的设置页(ADR-0010;内容和说明文字参考 MAIO 的 ClipboardSettingsView)。
/// 每改一项立即保存;运行中的功能通过设置的 Changed 事件实时生效,不需要重启。
/// </summary>
internal sealed class ClipboardSettingsView
{
    private static readonly (PopupPosition, string)[] Positions =
        [(PopupPosition.Caret, "文字光标处"), (PopupPosition.Mouse, "鼠标处"), (PopupPosition.Center, "屏幕中央")];

    private static readonly (CaretFallback, string)[] Fallbacks =
        [(CaretFallback.Center, "屏幕中央"), (CaretFallback.Mouse, "鼠标处")];

    private static readonly (SearchMode, string)[] SearchModes =
        [(SearchMode.Explicit, "按 Ctrl+F 才搜索"), (SearchMode.TypeToSearch, "直接打字搜索")];

    private static readonly (PreviewMode, string)[] PreviewModes =
        [(PreviewMode.Toggle, "按一下开,再按一下关"), (PreviewMode.Hold, "按住预览,松开关闭")];

    private static readonly (StorageLimit, string)[] Limits =
    [
        (StorageLimit.Mb500, "500 MB"), (StorageLimit.Gb1, "1 GB"), (StorageLimit.Gb2, "2 GB"), (StorageLimit.Gb5, "5 GB"),
        (StorageLimit.Gb10, "10 GB"), (StorageLimit.Gb20, "20 GB"), (StorageLimit.Unlimited, "永久保留"),
    ];

    private readonly FeatureSettingsContext _context;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private ClipboardSettings _value;

    private ClipboardSettingsView(FeatureSettingsContext context)
    {
        _context = context;
        _value = context.Settings.Get<ClipboardSettings>();
        Build();
    }

    public static UIElement Create(FeatureSettingsContext context) => new ClipboardSettingsView(context)._host;

    private void Build()
    {
        var v = _value;
        var fallback = SettingsUi.Card(
            "没有文字光标时", "当前不在输入框里,或程序不提供光标位置时放在哪",
            SettingsUi.Choice(Fallbacks, v.CaretFallback, x => Save(s => s.CaretFallback = x)));
        fallback.Visibility = Visible(v.PopupPosition == PopupPosition.Caret);

        _host.Content = SettingsUi.Stack(
            SettingsUi.Section("面板"),
            SettingsUi.Card(
                "面板里的按键",
                SettingsUi.Lines(
                    "↑↓ 选择,⏎ 粘贴,⇧⏎ 粘贴纯文本,Ctrl+1~9 快速粘贴第几条",
                    "空格 预览,Ctrl+P 固定,Del 删除",
                    "Ctrl+F 搜索,Esc 关闭")),
            SettingsUi.Card(
                "弹出位置",
                SettingsUi.Lines(
                    "文字光标处:在正在输入的位置下方弹出,浏览器、终端也支持",
                    "个别程序拿不到光标位置,这时按下一项处理"),
                SettingsUi.Choice(Positions, v.PopupPosition, x =>
                {
                    fallback.Visibility = Visible(x == PopupPosition.Caret);
                    Save(s => s.PopupPosition = x);
                })),
            fallback,
            SettingsUi.Card(
                "搜索方式",
                SettingsUi.Lines(
                    "按 Ctrl+F 才搜索:打开后方向键直接选择;用输入法打中文时,确认拼音的回车不会误粘贴",
                    "直接打字搜索:打开就能输入,回车立即粘贴"),
                SettingsUi.Choice(SearchModes, v.SearchMode, x => Save(s => s.SearchMode = x))),
            SettingsUi.Card(
                "空格预览",
                "预览文字全文、图片大图、文件列表",
                SettingsUi.Choice(PreviewModes, v.PreviewMode, x => Save(s => s.PreviewMode = x))),
            SettingsUi.Section("粘贴"),
            SettingsUi.Card(
                "选中后直接粘贴",
                SettingsUi.Lines(
                    "打开:回车后自动切回原来的程序并粘贴",
                    "关闭:只放进剪贴板,需要自己按 Ctrl+V"),
                SettingsUi.Toggle(v.PasteDirectly, x => Save(s => s.PasteDirectly = x))),
            SettingsUi.Card(
                "粘贴过的条目移到最前",
                "下次打开面板时默认选中它;关闭则按复制时间排序",
                SettingsUi.Toggle(v.MoveToTopOnPaste, x => Save(s => s.MoveToTopOnPaste = x))),
            SettingsUi.Section("记录"),
            SettingsUi.Card(
                "记录富文本格式", "RTF、HTML。关闭后只保存纯文字",
                SettingsUi.Toggle(v.RecordRichText, x => Save(s => s.RecordRichText = x))),
            SettingsUi.Card(
                "记录图片", "截图、复制的图片。图片较占空间,由下方的存储上限自动清理",
                SettingsUi.Toggle(v.RecordImages, x => Save(s => s.RecordImages = x))),
            SettingsUi.Card(
                "记录复制的文件", "只记录文件路径,不复制文件本身。原文件被删除或移动后就无法粘贴",
                SettingsUi.Toggle(v.RecordFiles, x => Save(s => s.RecordFiles = x))),
            SettingsUi.Card(
                "记录密码管理器标记的内容",
                SettingsUi.Lines(
                    "1Password、KeePass 等会把复制的密码标记为\"不要记录\"",
                    "打开后照样记录;数据只存在本机,没有加密"),
                SettingsUi.Toggle(!v.RespectExclusionFormats, x => Save(s => s.RespectExclusionFormats = !x))),
            SettingsUi.Section("存储"),
            SettingsUi.Card(
                "存储上限",
                SettingsUi.Lines(
                    "历史(不含固定的条目)超过上限时,自动删除最久没用过的",
                    "选\"永久保留\"则不自动删除"),
                SettingsUi.Choice(Limits, v.StorageLimit, x => Save(s => s.StorageLimit = x))),
            ClearHistoryCard(),
            SettingsUi.Card(
                "恢复默认设置", "把以上所有选项恢复成默认值(不影响历史记录)",
                SettingsUi.Button("恢复默认", () =>
                {
                    _value = new ClipboardSettings();
                    Save(_ => { });
                    Build();
                })));
    }

    private Border ClearHistoryCard()
    {
        Border card = null!;
        var button = SettingsUi.Button("清空…", () => _ = ClearHistoryAsync(card));
        card = SettingsUi.Card("清空历史", "删除所有未固定的记录;固定的条目保留", button);
        return card;
    }

    private async Task ClearHistoryAsync(FrameworkElement anchor)
    {
        var dialog = new ContentDialog
        {
            Title = "清空剪贴板历史?",
            Content = "所有未固定的记录都会被删除,无法恢复。固定的条目保留。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = anchor.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _context.Commands.PerformAsync(ClipboardFeature.ClearHistory, CommandArguments.Empty);
        }
        catch (CommandException ex)
        {
            _context.Logger.LogWarning(ex, "清空剪贴板历史失败");
            await new ContentDialog
            {
                Title = "无法清空",
                Content = "剪贴板功能没有启用。先在上方打开它再试。",
                CloseButtonText = "知道了",
                XamlRoot = anchor.XamlRoot,
            }.ShowAsync();
        }
    }

    /// <summary>改一项、立即保存。保存失败只记日志:界面上的值保留,下次改动会再尝试保存。</summary>
    private void Save(Action<ClipboardSettings> change)
    {
        change(_value);
        _ = SaveAsync(_value);
    }

    private async Task SaveAsync(ClipboardSettings value)
    {
        try
        {
            await _context.Settings.SaveAsync(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Logger.LogError(ex, "保存剪贴板设置失败");
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
