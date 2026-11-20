using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Waio.UI;

/// <summary>
/// 设置页的说明文字(参考 MAIO 的 NoteLines / PageIntro):
/// 说明一条一行,代替一整段长文字;每页(或每个标签)顶部一句话说清这一页是干什么的。
/// </summary>
public static partial class SettingsUi
{
    /// <summary>
    /// 把几条说明拼成卡片说明用的文字:<see cref="Card"/> 遇到多行说明时逐条显示(每条前面一个圆点,换行时悬挂缩进)。
    /// </summary>
    public static string Lines(params string[] lines) => string.Join("\n", lines);

    /// <summary>
    /// 一组说明,一条一行(小字、次要色、每条前面一个圆点)。
    /// 放在一组卡片下面当脚注,或者由 <see cref="Card"/> 用来显示多行说明。
    /// </summary>
    public static StackPanel Notes(params string[] lines)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(4, 8, 0, 4) };
        foreach (var line in lines)
        {
            panel.Children.Add(NoteLine(line));
        }

        return panel;
    }

    /// <summary>
    /// 页面(或标签)顶部的介绍:强调色图标 + 标题 + 一句话用途。
    /// </summary>
    /// <param name="glyph">Segoe Fluent Icons 的字符码。</param>
    /// <param name="notes">补充说明,在那句话下面逐条显示(可省略)。</param>
    public static Border PageIntro(string glyph, string title, string text, params string[] notes)
    {
        var intro = (Border)XamlReader.Load($$"""
            <Border {{Ns}} Padding="18,14" Margin="0,0,0,4" BorderThickness="1"
                CornerRadius="{ThemeResource WaioCardCornerRadius}"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                <Grid ColumnSpacing="14">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <FontIcon x:Name="Icon" FontSize="20" VerticalAlignment="Top" Margin="0,2,0,0"
                        Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" />
                    <StackPanel x:Name="Body" Grid.Column="1" Spacing="2">
                        <TextBlock x:Name="Title" Style="{StaticResource BodyStrongTextBlockStyle}" />
                        <TextBlock x:Name="Text" TextWrapping="Wrap"
                            Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    </StackPanel>
                </Grid>
            </Border>
            """);
        ((FontIcon)intro.FindName("Icon")).Glyph = glyph;
        ((TextBlock)intro.FindName("Title")).Text = title;
        ((TextBlock)intro.FindName("Text")).Text = text;
        if (notes.Length > 0)
        {
            var list = Notes(notes);
            list.Margin = new Thickness(0, 6, 0, 0);
            ((StackPanel)intro.FindName("Body")).Children.Add(list);
        }

        return Waio.UI.Theming.GlassShape.Card(intro);
    }

    private static Grid NoteLine(string line)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bullet = SecondaryText("•");
        var text = SecondaryText(line);
        text.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(text, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock SecondaryText(string text)
    {
        var block = (TextBlock)XamlReader.Load(
            $"<TextBlock {Ns} FontSize='12' Foreground='{{ThemeResource TextFillColorSecondaryBrush}}' />");
        block.Text = text;
        return block;
    }

    /// <summary>卡片说明有多行时:收起单行说明,换成逐条显示的说明(<see cref="Card"/> 调用)。</summary>
    private static void ShowAsNotes(TextBlock description, string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || description.Parent is not StackPanel parent)
        {
            return;
        }

        var notes = new StackPanel { Spacing = 2 };
        foreach (var line in lines)
        {
            notes.Children.Add(NoteLine(line));
        }

        description.Visibility = Visibility.Collapsed;
        parent.Children.Add(notes);
    }
}
