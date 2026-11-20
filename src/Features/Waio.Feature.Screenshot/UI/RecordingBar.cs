using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Recording;
using Waio.UI;
using Waio.UI.Theming;
using Windows.Graphics;
using WinRT.Interop;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 录屏的控制条(MAIO RecordingBar):放在选区下方(放不下依次换到上方、内部,同截图工具栏)。
/// 开始前:MP4 / GIF、系统声音、麦克风、开始、取消;录制中:红点 + 时长、完成、放弃。
/// 不抢焦点、排除在截屏之外(录不进视频)。只在 UI 线程上使用。
/// </summary>
internal sealed class RecordingBar : Window
{
    /// <summary>用户在控制条上的选择(开始时交给录制)。</summary>
    internal sealed record Choices(RecordingFormat Format, bool SystemAudio, bool Microphone);

    internal sealed record Actions(Action Start, Action Cancel, Action Finish, Action Discard, Action<Choices> Changed);

    private const double HeightDip = 48;
    private const double SetupWidthDip = 452;
    private const double RecordingWidthDip = 250;

    private readonly ToggleButton _mp4 = Toggle(null, "MP4");
    private readonly ToggleButton _gif = Toggle(null, "GIF");
    private readonly ToggleButton _system = Toggle("\uE767", "系统声音"); // Segoe Fluent Icons: Volume
    private readonly ToggleButton _microphone = Toggle("\uE720", "麦克风"); // Microphone
    private readonly StackPanel _setup = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _recording = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = new() { Text = "00:00", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, MinWidth = 54 };
    private readonly Actions _actions;
    private readonly PxRect _selection;
    private readonly PxRect _bounds;
    private readonly double _scale;
    private bool _updating;

    public RecordingBar(PxRect selection, PxRect displayBounds, double scale, Choices initial, Actions actions)
    {
        _selection = selection;
        _bounds = displayBounds;
        _scale = scale;
        _actions = actions;
        Title = "WAIO 录屏";

        _mp4.Click += (_, _) => Choose(RecordingFormat.Mp4);
        _gif.Click += (_, _) => Choose(RecordingFormat.Gif);
        _system.Click += (_, _) => Changed();
        _microphone.Click += (_, _) => Changed();
        ToolTipService.SetToolTip(_system, "录系统正在播放的声音(只有 MP4)");
        ToolTipService.SetToolTip(_microphone, "录麦克风(只有 MP4);和系统声音混成一条音轨");
        _setup.Children.Add(_mp4);
        _setup.Children.Add(_gif);
        _setup.Children.Add(Divider());
        _setup.Children.Add(_system);
        _setup.Children.Add(_microphone);
        _setup.Children.Add(Divider());
        _setup.Children.Add(ActionButton("开始", "开始录制(⏎)", actions.Start, accent: true));
        _setup.Children.Add(ActionButton("取消", "取消(Esc)", actions.Cancel));

        _recording.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 0xE8, 0x11, 0x23)), VerticalAlignment = VerticalAlignment.Center });
        _recording.Children.Add(_elapsed);
        _recording.Children.Add(ActionButton("完成", "停止并保存(再按一次录屏快捷键也可以)", actions.Finish, accent: true));
        _recording.Children.Add(ActionButton("放弃", "停止并删除这段录屏", actions.Discard));
        _recording.Visibility = Visibility.Collapsed;

        var root = new Grid { Padding = new Thickness(8, 0, 8, 0) };
        root.Children.Add(_setup);
        root.Children.Add(_recording);
        Content = root;

        WindowChrome.MakeOverlay(this, activatable: false);
        WindowChrome.MakePopupSurface(this, dark: Glass.IsDark());
        Glass.Attach(this, popup: true); // 磨砂玻璃,参数跟随外观设置(ADR-0026)
        _ = ScrollCaptureBorder.SetWindowDisplayAffinity(WindowNative.GetWindowHandle(this), 0x11); // WDA_EXCLUDEFROMCAPTURE
        Apply(initial);
        Place(SetupWidthDip);
    }

    public Choices Selection => new(_gif.IsChecked == true ? RecordingFormat.Gif : RecordingFormat.Mp4, _system.IsChecked == true, _microphone.IsChecked == true);

    public void ShowBar() => WindowChrome.ShowWithoutActivation(this);

    /// <summary>切换到"录制中"。</summary>
    public void ShowRecording()
    {
        _setup.Visibility = Visibility.Collapsed;
        _recording.Visibility = Visibility.Visible;
        Place(RecordingWidthDip);
    }

    public void SetElapsed(TimeSpan elapsed) => _elapsed.Text = RecordingGeometry.Elapsed(elapsed);

    private void Choose(RecordingFormat format)
    {
        Apply(Selection with { Format = format });
        Changed();
    }

    private void Changed()
    {
        if (!_updating)
        {
            _actions.Changed(Selection);
        }
    }

    private void Apply(Choices choices)
    {
        _updating = true;
        _mp4.IsChecked = choices.Format == RecordingFormat.Mp4;
        _gif.IsChecked = choices.Format == RecordingFormat.Gif;
        _system.IsChecked = choices.SystemAudio;
        _microphone.IsChecked = choices.Microphone;
        _system.IsEnabled = _microphone.IsEnabled = choices.Format == RecordingFormat.Mp4; // GIF 没有声音
        _updating = false;
    }

    private void Place(double widthDip)
    {
        var width = (int)Math.Round(widthDip * _scale);
        var height = (int)Math.Round(HeightDip * _scale);
        var frame = OverlayLayout.ToolbarFrame(width, height, _selection, _bounds, _scale);
        AppWindow.Move(new PointInt32(frame.X, frame.Y)); // 先挪到目标显示器上,再设尺寸(跨缩放的显示器时系统会按新 DPI 改尺寸)
        AppWindow.MoveAndResize(new RectInt32(frame.X, frame.Y, frame.Width, frame.Height));
    }

    /// <summary>
    /// 图标和文字分开放:图标用 FontIcon(图标字体),文字用默认字体。
    /// 不能把两者塞进同一个 TextBlock 再写"图标字体, Segoe UI"这样的字体列表——那样文字全显示成方框(2026-11-11 用户反馈)。
    /// </summary>
    private static ToggleButton Toggle(string? glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (glyph is not null)
        {
            content.Children.Add(new FontIcon
            {
                Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            });
        }

        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return new ToggleButton { Content = content, Padding = new Thickness(10, 4, 10, 5), VerticalAlignment = VerticalAlignment.Center };
    }

    private static Button ActionButton(string text, string tip, Action action, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 5), VerticalAlignment = VerticalAlignment.Center };
        if (accent)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static Rectangle Divider() => new()
    {
        Width = 1, Height = 22, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
        Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };
}
