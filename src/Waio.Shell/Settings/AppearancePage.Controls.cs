using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Waio.UI.Theming;
using Windows.UI;

namespace Waio.Shell.Settings;

/// <summary>"外观"页用到的控件:滑块、取色按钮。</summary>
internal sealed partial class AppearancePage
{
    /// <summary>
    /// 宽度固定的滑块。值在内部按 <paramref name="factor"/> 放大成整数刻度(如 0.55 → 55%),
    /// 提示文字用 <paramref name="format"/>(如 "{0:0}%")。
    /// </summary>
    private static Slider Slider(double value, double minimum, double maximum, string format, double factor, Action<double> changed)
    {
        var slider = new Slider
        {
            Minimum = Math.Round(minimum * factor), Maximum = Math.Round(maximum * factor), StepFrequency = 1, Width = 220,
            Value = Math.Round(value * factor), VerticalAlignment = VerticalAlignment.Center,
            ThumbToolTipValueConverter = new FormatText(format),
        };
        slider.ValueChanged += (_, e) => changed(e.NewValue / factor);
        return slider;
    }
    /// <summary>
    /// 取色:按钮上是色块 + 色值,点开是系统的取色器(可输入十六进制),选的同时就生效;旁边"重置"清除自定义。
    /// <paramref name="current"/> 为 null 表示没自定义,显示 <paramref name="defaultTitle"/> 和实际生效的颜色。
    /// </summary>
    private static UIElement ColorSetting(string? current, string defaultTitle, Func<Color> effective, Action<string?> changed)
    {
        var custom = ColorText.TryParse(current, out var parsed);
        var swatch = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = new SolidColorBrush(custom ? parsed : effective()),
        };
        var label = new TextBlock { Text = custom ? ColorText.Format(parsed) : defaultTitle, VerticalAlignment = VerticalAlignment.Center };
        var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        face.Children.Add(swatch);
        face.Children.Add(label);

        // "重置"只在自定义过颜色时可点;不可点时只是变淡,仍占着位置(出现 / 消失不让卡片位移)
        var reset = new HyperlinkButton { Content = "重置", Padding = new Thickness(8, 4, 8, 4) };
        void ShowReset(bool on)
        {
            reset.IsEnabled = on;
            reset.Opacity = on ? 1 : 0; // 不可点时完全隐藏但仍占位
        }

        ShowReset(custom);
        var picker = new ColorPicker
        {
            Color = custom ? parsed : effective(),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
        };
        picker.ColorChanged += (_, e) =>
        {
            var hex = ColorText.Format(e.NewColor);
            swatch.Background = new SolidColorBrush(e.NewColor);
            label.Text = hex;
            changed(hex);
            ShowReset(true);
        };

        // 宽度固定:色值文字和"跟随系统"长短不同,按钮不能因此变宽,否则卡片会位移
        var button = new Button { Content = face, Width = 132, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Bottom };

        reset.Click += (_, _) =>
        {
            ShowReset(false);
            label.Text = defaultTitle;
            changed(null);
            swatch.Background = new SolidColorBrush(effective()); // changed 之后 Glass.Current 已更新
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(button);
        row.Children.Add(reset);
        return row;
    }

    private sealed partial class FormatText(string format) : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            string.Format(System.Globalization.CultureInfo.CurrentCulture, format, value);

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}