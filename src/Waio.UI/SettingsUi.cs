using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Waio.UI;

/// <summary>
/// 设置页构件(ADR-0010,外观见 ADR-0026 磨砂玻璃):分组标题、卡片行(标题 + 说明 + 右侧控件)、开关、下拉框。
/// 设置窗口和各功能的设置页都用它,保证所有设置页长得一样。
/// 卡片外壳用 XAML 字符串构造,这样颜色能用 <c>ThemeResource</c>,自动跟随深浅色、高对比度。
/// </summary>
public static class SettingsUi
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
        + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    /// <summary>
    /// 纵向排列的整页内容(设置页的根)。连续的几张 <see cref="Card"/> 自动合成一组:
    /// 同一块玻璃底、行与行之间一条细线(ADR-0026,不再是一行一张独立卡片的 Windows 设置页样式)。
    /// </summary>
    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 4 };
        StackPanel? group = null;
        foreach (var child in children)
        {
            if (child is Border card && Cards.TryGetValue(card, out _))
            {
                if (group is null)
                {
                    var shell = Waio.UI.Theming.GlassShape.Card(Group());
                    group = (StackPanel)shell.Child;
                    panel.Children.Add(shell);
                }

                // 组里的卡片:不要自己的底色和圆角;除第一行外,上边框就是分隔线(颜色仍是卡片描边色,跟随主题)
                card.Background = null;
                Waio.UI.Theming.GlassShape.Release(card);
                card.CornerRadius = default;
                card.BorderThickness = group.Children.Count == 0 ? default : new Thickness(0, 1, 0, 0);
                group.Children.Add(card);
                continue;
            }

            group = null;
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>由 <see cref="Card"/> 创建的卡片(用来在 <see cref="Stack"/> 里认出它们)。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, object> Cards = [];

    private static readonly object Marker = new();

    /// <summary>一组卡片的玻璃底。</summary>
    private static Border Group() => (Border)XamlReader.Load($$"""
        <Border {{Ns}} BorderThickness="1"
            CornerRadius="{ThemeResource WaioCardCornerRadius}"
            Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
            <StackPanel />
        </Border>
        """);

    /// <summary>分组标题(如"面板""记录")。</summary>
    public static TextBlock Section(string title)
    {
        var block = (TextBlock)XamlReader.Load(
            $"<TextBlock {Ns} Style='{{StaticResource BodyStrongTextBlockStyle}}' Margin='4,22,0,8' />");
        block.Text = title;
        return block;
    }

    /// <summary>
    /// 一张卡片:左边标题和说明,右边控件。<paramref name="action"/> 为 null 时只显示文字。
    /// </summary>
    public static Border Card(string header, string? description = null, UIElement? action = null)
    {
        var card = (Border)XamlReader.Load($$"""
            <Border {{Ns}}
                Padding="18,12" MinHeight="64" BorderThickness="1"
                CornerRadius="{ThemeResource WaioCardCornerRadius}"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                <Grid ColumnSpacing="16">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <StackPanel VerticalAlignment="Center" Spacing="2">
                        <TextBlock x:Name="Header" TextWrapping="Wrap" />
                        <TextBlock x:Name="Description" FontSize="12" TextWrapping="Wrap"
                            Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    </StackPanel>
                    <ContentPresenter x:Name="Action" Grid.Column="1" VerticalAlignment="Center" />
                </Grid>
            </Border>
            """);
        ((TextBlock)card.FindName("Header")).Text = header;
        var text = (TextBlock)card.FindName("Description");
        text.Text = description ?? "";
        text.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        ((ContentPresenter)card.FindName("Action")).Content = action;
        Cards.AddOrUpdate(card, Marker);
        return Waio.UI.Theming.GlassShape.Card(card);
    }

    /// <summary>开关。只在用户拨动时回调(初始值不回调)。</summary>
    public static ToggleSwitch Toggle(bool isOn, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "开", OffContent = "关", MinWidth = 0 };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    /// <summary>下拉选择。只在用户改选时回调。</summary>
    public static ComboBox Choice<T>(IReadOnlyList<(T Value, string Title)> options, T selected, Action<T> changed)
    {
        // 宽度按最长的选项固定:WinUI 的下拉框宽度跟着"当前选中项"变,改选后卡片右侧变宽变窄,
        // 左边说明重新换行、卡片变高,下面整页都会跳一下(设置页不要位移)
        var widest = 0.0;
        var box = new ComboBox();
        foreach (var (_, title) in options)
        {
            box.Items.Add(title);
            widest = Math.Max(widest, TextWidth(title));
        }

        box.Width = Math.Max(180, Math.Ceiling(widest) + ComboChrome);

        box.SelectedIndex = Math.Max(0, options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, selected)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                changed(options[box.SelectedIndex].Value);
            }
        };
        return box;
    }

    /// <summary>下拉框除文字以外占的宽度:左内边距 12 + 右边箭头区 38 + 边框和余量。</summary>
    private const double ComboChrome = 60;

    /// <summary>按控件默认字号(14)量一段文字的宽度。</summary>
    private static double TextWidth(string text)
    {
        try
        {
            var block = new TextBlock { Text = text, FontSize = 14 };
            block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
        catch (Exception)
        {
            return text.Length * 14.0; // 量不了时按每字 14 粗估(中文一个字约 14)
        }
    }

    /// <summary>键帽样式的快捷键显示,如"Win + V"。</summary>
    public static Border KeyCap(string text)
    {
        var cap = (Border)XamlReader.Load($$"""
            <Border {{Ns}} Padding="8,3" CornerRadius="{ThemeResource WaioControlCornerRadius}" BorderThickness="1"
                Background="{ThemeResource ControlFillColorDefaultBrush}"
                BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}">
                <TextBlock x:Name="Text" FontSize="12" FontWeight="SemiBold" />
            </Border>
            """);
        ((TextBlock)cap.FindName("Text")).Text = text;
        return Waio.UI.Theming.GlassShape.Control(cap);
    }

    /// <summary>状态徽标的语气:决定颜色。</summary>
    public enum Tone
    {
        Neutral,
        Caution,
        Critical,
        Success,
    }

    /// <summary>小圆角徽标,如"冲突""覆盖系统快捷键""不可用"。</summary>
    public static Border Badge(string text, Tone tone)
    {
        var (background, foreground) = tone switch
        {
            Tone.Caution => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            Tone.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            Tone.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            _ => ("SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };
        var badge = (Border)XamlReader.Load($$"""
            <Border {{Ns}} Padding="8,2,8,3" CornerRadius="10" VerticalAlignment="Center"
                Background="{ThemeResource {{background}}}">
                <TextBlock x:Name="Text" FontSize="11" Foreground="{ThemeResource {{foreground}}}" />
            </Border>
            """);
        ((TextBlock)badge.FindName("Text")).Text = text;
        return badge;
    }

    /// <summary>次要文字(灰色),用于"未设置"之类的提示。</summary>
    public static TextBlock Hint(string text)
    {
        var block = (TextBlock)XamlReader.Load(
            $"<TextBlock {Ns} Foreground='{{ThemeResource TextFillColorTertiaryBrush}}' VerticalAlignment='Center' />");
        block.Text = text;
        return block;
    }

    /// <summary>普通按钮。</summary>
    public static Button Button(string text, Action click)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => click();
        return button;
    }
}
