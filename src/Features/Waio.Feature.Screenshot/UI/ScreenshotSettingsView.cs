using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 截图设置页(MAIO ScreenshotSettingsView,选项与说明文字一致,⌘ 换成 Ctrl)。每改一项立即保存;运行中的功能每次截图都重新读设置。
/// 选项:识别控件、放大镜、颜色格式、保存、贴图、录屏、截图历史(上限、手动清理见 <c>.History.cs</c>)。
/// </summary>
internal sealed partial class ScreenshotSettingsView
{
    private static readonly (ColorFormat, string)[] Formats = [(ColorFormat.Hex, ColorFormat.Hex.Title()), (ColorFormat.Rgb, ColorFormat.Rgb.Title())];
    private static readonly (int, string)[] Mp4Rates = [(15, "15 帧/秒"), (30, "30 帧/秒"), (60, "60 帧/秒")];
    private static readonly (int, string)[] GifRates = [(10, "10 帧/秒"), (15, "15 帧/秒"), (20, "20 帧/秒"), (30, "30 帧/秒")];
    private static readonly (HistoryLimit, string)[] Limits = [.. Enum.GetValues<HistoryLimit>().Select(l => (l, l.Title()))];

    private readonly FeatureSettingsContext _context;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _usage = SettingsUi.Hint("");

    private ScreenshotSettingsView(FeatureSettingsContext context)
    {
        _context = context;
        Build();
        _ = LoadUsageAsync();
    }

    public static UIElement Create(FeatureSettingsContext context) => new ScreenshotSettingsView(context)._host;

    private ScreenshotSettings Value => _context.Settings.Get<ScreenshotSettings>();

    private void Build()
    {
        var v = Value;
        var folder = new TextBox
        {
            Text = v.SaveDirectory, PlaceholderText = ImageExporter.DefaultDirectory(), MinWidth = 320,
        };
        folder.LostFocus += (_, _) => Save(s => s.SaveDirectory = folder.Text.Trim());
        var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderActions.Children.Add(folder);
        folderActions.Children.Add(SettingsUi.Button("恢复默认", () =>
        {
            folder.Text = "";
            Save(s => s.SaveDirectory = "");
        }));

        _host.Content = SettingsUi.Stack([
            SettingsUi.Section("截图"),
            SettingsUi.Card("截图时的按键", Help.Keys),
            SettingsUi.Card("总是识别控件", Help.Controls, SettingsUi.Toggle(v.AlwaysDetectControls, x => Save(s => s.AlwaysDetectControls = x))),
            SettingsUi.Card("显示放大镜", null, SettingsUi.Toggle(v.ShowMagnifier, x => Save(s => s.ShowMagnifier = x))),
            SettingsUi.Card("颜色格式", "截图时单独按一下 Shift 也可以切换", SettingsUi.Choice(Formats, v.ColorFormat, x => Save(s => s.ColorFormat = x))),
            SettingsUi.Section("保存"),
            SettingsUi.Card("保存文件夹", Help.Folder, folderActions),
            SettingsUi.Card("Ctrl+S 时询问保存位置", null, SettingsUi.Toggle(v.AskSaveLocation, x => Save(s => s.AskSaveLocation = x))),
            SettingsUi.Card("复制截图时同时保存到文件夹", Help.AutoSave, SettingsUi.Toggle(v.AutoSave, x => Save(s => s.AutoSave = x))),
            SettingsUi.Section("贴图"),
            SettingsUi.Card("贴图显示阴影", Help.PinShadow, SettingsUi.Toggle(v.PinShadow, x => Save(s => s.PinShadow = x))),
            SettingsUi.Card("贴图上可以选择文字", Help.PinText, SettingsUi.Toggle(v.PinSelectableText, x => Save(s => s.PinSelectableText = x))),
            SettingsUi.Card("贴图缩放:修饰键 + 滚轮 / - =", Help.PinKeys, SettingsUi.Choice(Modifiers, v.PinZoomModifier, x => SaveModifier(s => s.PinZoomModifier = x))),
            SettingsUi.Card("贴图透明度:修饰键 + 滚轮 / - =", null, SettingsUi.Choice(Modifiers, v.PinOpacityModifier, x => SaveModifier(s => s.PinOpacityModifier = x))),
            _modifierWarning = NewModifierWarning(),
            SettingsUi.Section("录屏"),
            SettingsUi.Card("录屏", Help.Recording),
            SettingsUi.Card("录屏保存文件夹", "文件名形如 WAIO 录屏 2026-11-11 14.05.33.mp4", RecordingFolder(v)),
            SettingsUi.Card("MP4 帧率", "画面没变化时不写新帧,静止的画面几乎不占空间", SettingsUi.Choice(Mp4Rates, v.RecordingFps, x => Save(s => s.RecordingFps = x))),
            SettingsUi.Card("GIF 帧率", null, SettingsUi.Choice(GifRates, v.GifFps, x => Save(s => s.GifFps = x))),
            SettingsUi.Card("GIF 高清", Help.GifQuality, SettingsUi.Toggle(v.GifHighQuality, x => Save(s => s.GifHighQuality = x))),
            SettingsUi.Card("录进鼠标指针", null, SettingsUi.Toggle(v.RecordCursor, x => Save(s => s.RecordCursor = x))),
            SettingsUi.Section("截图历史"),
            SettingsUi.Card("截图历史", Help.History, OpenHistoryButton()),
            SettingsUi.Card("最多保留", null, SettingsUi.Choice(Limits, v.HistoryLimit, x => Save(s => s.HistoryLimit = x))),
            SettingsUi.Card("当前占用", null, _usage),
            CleanupCard(),
            SettingsUi.Card("恢复默认设置", "保存文件夹和历史上限也会恢复", SettingsUi.Button("恢复默认", () =>
            {
                _ = _context.Settings.SaveAsync(new ScreenshotSettings()).AsTask();
                Build();
            })),
        ]);
    }

    private StackPanel RecordingFolder(ScreenshotSettings v)
    {
        var folder = new TextBox { Text = v.RecordingDirectory, PlaceholderText = ImageExporter.DefaultRecordingDirectory(), MinWidth = 320 };
        folder.LostFocus += (_, _) => Save(s => s.RecordingDirectory = folder.Text.Trim());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(folder);
        actions.Children.Add(SettingsUi.Button("恢复默认", () =>
        {
            folder.Text = "";
            Save(s => s.RecordingDirectory = "");
        }));
        return actions;
    }

    private async Task LoadUsageAsync()
    {
        try
        {
            var store = HistoryStore.For(_context.Storage.DataDirectory, _context.Logger);
            var (count, bytes, pinned) = await store.StatsAsync();
            _usage.Text = $"{count} 张,占用 {bytes / 1_000_000.0:0.#} MB" + (pinned > 0 ? $",收藏 {pinned} 张" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _usage.Text = "读取失败";
        }
    }

    private void Save(Action<ScreenshotSettings> change)
    {
        var settings = Value;
        change(settings);
        _ = _context.Settings.SaveAsync(settings).AsTask();
    }

    /// <summary>说明文字(MAIO ScreenshotHelp)。</summary>
    private static class Help
    {
        public const string Keys =
            "按下后先冻结整个屏幕,再在静止的画面上框选,所以打开的菜单、悬浮提示也能截进去。\n"
            + "截图时:拖动框选;单击选中高亮的窗口;Ctrl+A 全屏;方向键移动 1 像素,Shift+方向键调整大小;"
            + "⏎ 或双击复制;Ctrl+S 保存;Ctrl+P 贴图;C 复制颜色;Shift 切换颜色格式;Esc 或右键先取消选区,再退出。";

        public const string Controls = "关闭时,鼠标悬停高亮的是窗口,按住 Ctrl 才高亮按钮、输入框等控件。打开后一直识别控件。";

        public const string Folder = "Ctrl+S 保存截图的位置。文件名形如 WAIO 2026-11-03 14.05.33.png。";

        public const string PinShadow = "贴图四周显示系统主题色的发光,和背景区分开。关掉后贴图就是一张纯粹的图,可以和背景融为一体。";

        public const string PinText =
            "贴图后在后台识别文字(离线,一般不到 1 秒),鼠标移到文字上变成 I 形光标,拖动选择、双击选中一段、Ctrl+A 全选,Ctrl+C 复制选中的文字;"
            + "在没有文字的地方拖动仍然是移动贴图。只对之后新贴的图生效。";

        public const string PinKeys =
            "在贴图上按住修饰键滚动滚轮(或按修饰键 + - / =),每次缩放或调整透明度 5%。默认直接滚轮(或 - =)缩放,Ctrl + 滚轮(或 Ctrl+- Ctrl+=)调透明度。\n"
            + "缩放以鼠标为中心。数字键 1~9、0 直接把透明度设为 10%~90%、100%。"
            + "贴图上还可以:拖动移动,R / Shift+R 旋转,E 编辑标注,Ctrl+C 复制,Ctrl+S 保存,Esc 或双击关闭,右键打开菜单。";

        public const string History =
            "每次截图、贴图都会保存到截图历史(包括可以重新编辑的标注),在历史里可以重新贴图、复制、编辑、删除。\n"
            + "超过上限时自动删除最旧的;收藏的不会被删除。";

        public const string Recording =
            "按 Alt+F3(或截图时 Ctrl+R、工具栏的录屏按钮)框选区域,在下方的控制条上选 MP4 / GIF、系统声音、麦克风,点开始(或 ⏎)。"
            + "录制中按键照常打到其他程序;点完成或再按一次 Alt+F3 结束。录完保存到下面的文件夹,并把文件复制到剪贴板。录屏不进截图历史。";

        public const string GifQuality = "默认按系统缩放后的大小录(150% 缩放时宽高是屏幕像素的 2/3,文件小很多);打开后按屏幕像素录。";

        public const string AutoSave = "默认只复制并写入截图历史。打开后每次复制截图也会在保存文件夹里存一份 PNG。";
    }
}
