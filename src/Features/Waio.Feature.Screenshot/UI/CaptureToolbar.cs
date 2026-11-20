using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure.Rendering;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 选区确定后出现的工具栏(MAIO CaptureToolbar,spec 4.1):第一行是工具和操作;第二行是颜色和大小;当前工具有专属选项时才出现第三行
/// (第三行的选项见 <c>.Options.cs</c>)。按钮不抢键盘焦点,截图时的快捷键照常可用。
/// </summary>
internal sealed partial class CaptureToolbar
{
    /// <summary>第三行(含行间距)的高度(DIP)。工具栏按"三行"的高度定位,第三行出现、消失时前两行不动。</summary>
    public const double OptionsRowHeight = 30;

    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
    private static readonly SolidColorBrush SelectedBrush = new(ColorHelper.FromArgb(0x5A, 0x00, 0x78, 0xD4));

    private readonly Dictionary<AnnotationTool, Button> _tools = [];
    private readonly Button _undo;
    private readonly Button _scrolling;
    private readonly Button _record;
    private readonly Button _redo;
    private readonly Button _copy;
    private readonly List<(Button Button, Border Ring, AnnotationColor Color)> _swatches = [];
    private readonly StackPanel _styleRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Slider _slider = new() { Width = 80, StepFrequency = 1, IsThumbToolTipEnabled = false, AllowFocusOnInteraction = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Microsoft.UI.Xaml.Controls.TextBox _sizeBox = new() { Width = 46, MinWidth = 0, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _optionsRow = new() { Orientation = Orientation.Horizontal, Spacing = 4, Height = OptionsRowHeight - 4 };
    private ToolbarState? _state;
    private bool _updating;

    public CaptureToolbar()
    {
        var first = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        foreach (var tool in AnnotationTools.All)
        {
            var button = IconButton(Glyph(tool), $"{tool.Title()}({tool.Key()})", new ToolbarCommand.Tool(tool));
            if (tool == AnnotationTool.Counter)
            {
                button.Content = new TextBlock { Text = "①", FontSize = 17, Foreground = new SolidColorBrush(Colors.White) };
            }

            _tools[tool] = button;
            first.Children.Add(button);
        }

        first.Children.Add(Divider());
        first.Children.Add(_undo = IconButton("", "撤销(Ctrl+Z)", new ToolbarCommand.Undo()));
        first.Children.Add(_redo = IconButton("", "重做(Ctrl+Shift+Z / Ctrl+Y)", new ToolbarCommand.Redo()));
        first.Children.Add(Divider());
        first.Children.Add(_scrolling = IconButton("\uE74B", "长截图(Ctrl+L):自己用滚轮往下滚,自动拼接", new ToolbarCommand.Scrolling())); // Segoe Fluent Icons: Down
        first.Children.Add(_record = IconButton("\uE714", "录屏(Ctrl+R):录 MP4 或 GIF,MP4 可以录系统声音和麦克风", new ToolbarCommand.Record())); // Segoe Fluent Icons: Video
        first.Children.Add(IconButton("\uE8A5", "\u8BC6\u522B\u6587\u5B57(Ctrl+O)", new ToolbarCommand.Ocr())); // Segoe Fluent Icons: Document
        first.Children.Add(IconButton("\uE8C1", "\u622A\u56FE\u7FFB\u8BD1(Ctrl+T)", new ToolbarCommand.Translate())); // Characters,\u4E0E\u7FFB\u8BD1\u529F\u80FD\u7684\u56FE\u6807\u4E00\u81F4
        first.Children.Add(Divider());
        first.Children.Add(IconButton("\uE718", "贴图(Ctrl+P),同时复制", new ToolbarCommand.Pin())); // Segoe Fluent Icons: Pin
        first.Children.Add(IconButton("", "保存(Ctrl+S)", new ToolbarCommand.Save()));
        first.Children.Add(IconButton("", "取消(Esc)", new ToolbarCommand.Cancel()));
        first.Children.Add(_copy = IconButton("", "复制(⏎)", new ToolbarCommand.Copy(), accent: true));

        BuildStyleRow();
        var rows = new StackPanel { Spacing = 4 };
        rows.Children.Add(first);
        rows.Children.Add(_styleRow);
        rows.Children.Add(_optionsRow);
        Root = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF2, 0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 5, 6, 5),
            Child = rows,
            RequestedTheme = ElementTheme.Dark,
            Visibility = Visibility.Collapsed,
        };

        // 点在工具栏的空白处不能变成"在这里重新框选"
        Root.PointerPressed += (_, e) => e.Handled = true;
        Root.PointerReleased += (_, e) => e.Handled = true;
    }

    public Border Root { get; }

    /// <summary>点了按钮、改了颜色或大小。</summary>
    public event Action<ToolbarCommand>? Command;

    /// <summary>输入框、颜色面板用完后,把键盘焦点还给遮罩。</summary>
    public event Action? FocusReturned;

    /// <summary>不含第三行的大小(DIP)。</summary>
    public Windows.Foundation.Size MeasureBase()
    {
        var visibility = _optionsRow.Visibility;
        _optionsRow.Visibility = Visibility.Collapsed;
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Root.DesiredSize;
        _optionsRow.Visibility = visibility;
        return size;
    }

    public void Update(ToolbarState state)
    {
        _updating = true;
        try
        {
            foreach (var (tool, button) in _tools)
            {
                button.Background = state.Tool == tool ? SelectedBrush : Transparent;
            }

            _undo.IsEnabled = state.CanUndo;
            _redo.IsEnabled = state.CanRedo;
            _scrolling.IsEnabled = state.CapturesScreen; // 重新编辑已有截图时不能长截图、录屏(选区是图片,不是屏幕)
            _record.IsEnabled = state.CapturesScreen;
            ToolTipService.SetToolTip(_copy, state.ConfirmHelp);
            var active = state.ActiveTool is not null;
            _styleRow.Opacity = active ? 1 : 0.35;
            _styleRow.IsHitTestVisible = active;
            var usesColor = state.ActiveTool?.UsesColor() ?? true;
            foreach (var (button, ring, color) in _swatches)
            {
                ring.BorderBrush = usesColor && color == state.PaletteColor ? new SolidColorBrush(Colors.White) : Transparent;
                button.Opacity = usesColor ? 1 : 0.35;
                button.IsHitTestVisible = usesColor;
            }

            _slider.Minimum = state.SizeRange.Min;
            _slider.Maximum = state.SizeRange.Max;
            _slider.Value = state.Size;
            if (_sizeBox.FocusState == FocusState.Unfocused)
            {
                _sizeBox.Text = Math.Round(state.Size).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            UpdateOptions(state);
            _state = state;
        }
        finally
        {
            _updating = false;
        }
    }

    private void BuildStyleRow()
    {
        foreach (var color in AnnotationColors.Presets)
        {
            var dot = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(AnnotationRenderer.ToColor(color)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
            };
            var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), Child = dot };
            var button = Bare(ring, color.Name, new ToolbarCommand.Color(color));
            _swatches.Add((button, ring, color));
            _styleRow.Children.Add(button);
        }

        _styleRow.Children.Add(CustomColorButton());
        _styleRow.Children.Add(Divider());
        _styleRow.Children.Add(new FontIcon { Glyph = "", FontFamily = new FontFamily(IconFont), FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        const string sizeHelp = "大小(线宽、字号等),可以拖动、输入数字或用滚轮调整";
        ToolTipService.SetToolTip(_slider, sizeHelp);
        ToolTipService.SetToolTip(_sizeBox, sizeHelp);
        _slider.ValueChanged += (_, e) =>
        {
            if (!_updating && Math.Abs(e.NewValue - (_state?.Size ?? 0)) >= 0.5)
            {
                Command?.Invoke(new ToolbarCommand.Size(e.NewValue));
            }
        };
        _sizeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                CommitSizeBox();
                FocusReturned?.Invoke();
                e.Handled = true;
            }
        };
        _sizeBox.LostFocus += (_, _) => CommitSizeBox();
        _styleRow.Children.Add(_slider);
        _styleRow.Children.Add(_sizeBox);
    }

    /// <summary>自定义颜色(MAIO 打开系统颜色面板;这里是颜色选择器浮层)。</summary>
    private Button CustomColorButton()
    {
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring };
        var flyout = new Flyout { Content = picker };
        picker.ColorChanged += (_, e) =>
        {
            if (!_updating)
            {
                Command?.Invoke(new ToolbarCommand.Color(new AnnotationColor(e.NewColor.R / 255.0, e.NewColor.G / 255.0, e.NewColor.B / 255.0)));
            }
        };
        flyout.Opening += (_, _) =>
        {
            if (_state is { } state)
            {
                _updating = true;
                picker.Color = AnnotationRenderer.ToColor(state.PaletteColor);
                _updating = false;
            }
        };
        flyout.Closed += (_, _) => FocusReturned?.Invoke();
        var button = IconButton("", "自定义颜色", null);
        button.Flyout = flyout;
        return button;
    }

    private void CommitSizeBox()
    {
        if (double.TryParse(_sizeBox.Text.Trim(), out var value))
        {
            Command?.Invoke(new ToolbarCommand.Size(value));
        }

        if (_state is { } state)
        {
            _sizeBox.Text = Math.Round(state.Size).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private Button IconButton(string glyph, string help, ToolbarCommand? command, bool accent = false)
    {
        var icon = new FontIcon
        {
            Glyph = glyph, FontFamily = new FontFamily(IconFont), FontSize = 16,
            Foreground = accent ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)) : new SolidColorBrush(Colors.White),
        };
        var button = Bare(icon, help, command);
        button.Width = 32;
        button.Height = 30;
        return button;
    }

    private Button Bare(UIElement content, string help, ToolbarCommand? command)
    {
        var button = new Button
        {
            Content = content, Background = Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4),
            MinWidth = 0, MinHeight = 0, AllowFocusOnInteraction = false, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, help);
        if (command is not null)
        {
            button.Click += (_, _) => Command?.Invoke(command);
        }

        return button;
    }

    private static Border Divider() => new()
    {
        Width = 1, Height = 18, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
    };

    private static string Glyph(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => "",
        AnnotationTool.Ellipse => "",
        AnnotationTool.Arrow => "",
        AnnotationTool.Line => "",
        AnnotationTool.Pen => "",
        AnnotationTool.Highlighter => "",
        AnnotationTool.Text => "",
        AnnotationTool.Mosaic => "",
        AnnotationTool.Blur => "",
        _ => "",
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = new SolidColorBrush(Colors.White),
        FontWeight = FontWeights.Normal,
    };
}
