using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure.Rendering;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 工具栏第三行:各工具特有的选项(MAIO ToolSpecificOptions / TextOptions)。
/// 矩形:实心、圆角;椭圆:实心;直线:虚线;文字:粗体、外观(无 / 描边 / 底色,三选一)、色板改文字还是背景;序号:下一个编号。
/// </summary>
internal sealed partial class CaptureToolbar
{
    private static readonly SolidColorBrush ChipBrush = new(ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush ChipOnBrush = new(ColorHelper.FromArgb(0x80, 0x00, 0x78, 0xD4));

    private static string Title(TextDecoration decoration) => decoration switch
    {
        TextDecoration.Outline => "描边",
        TextDecoration.Background => "底色",
        _ => "无",
    };

    /// <summary>当前工具有选项时显示第三行。工具变了才重建,状态变了只刷新。</summary>
    private void UpdateOptions(ToolbarState state)
    {
        var tool = state.ActiveTool is { } active && active.HasOptions() ? active : (AnnotationTool?)null;
        _optionsRow.Visibility = tool is null ? Visibility.Collapsed : Visibility.Visible;
        if (tool is null)
        {
            _optionsRow.Children.Clear();
            return;
        }

        // 每次都按状态重建:控件很少,重建比逐个同步简单可靠
        _optionsRow.Children.Clear();
        switch (tool)
        {
            case AnnotationTool.Rectangle:
                _optionsRow.Children.Add(Chip("实心", "实心填充", state.Filled, new ToolbarCommand.Option(ToolOption.Filled)));
                _optionsRow.Children.Add(Chip("圆角", "圆角矩形", state.Rounded, new ToolbarCommand.Option(ToolOption.Rounded)));
                break;
            case AnnotationTool.Ellipse:
                _optionsRow.Children.Add(Chip("实心", "实心填充", state.Filled, new ToolbarCommand.Option(ToolOption.Filled)));
                break;
            case AnnotationTool.Line:
                _optionsRow.Children.Add(Chip("虚线", "画虚线", state.Dashed, new ToolbarCommand.Option(ToolOption.Dashed)));
                break;
            case AnnotationTool.Text:
                AddTextOptions(state);
                break;
            case AnnotationTool.Counter:
                _optionsRow.Children.Add(Label($"下一个 {state.NextCounter}"));
                var hint = Label("滚轮调整");
                hint.Opacity = 0.6;
                _optionsRow.Children.Add(hint);
                break;
        }
    }

    private void AddTextOptions(ToolbarState state)
    {
        var bold = Chip("", "粗体", state.Text.Bold, new ToolbarCommand.TextBold());
        ((TextBlock)bold.Content).FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        ((TextBlock)bold.Content).Text = "B";
        _optionsRow.Children.Add(bold);

        // 无 / 描边 / 底色:一个分段控件,表示三者只能选一个
        var segmented = new StackPanel { Orientation = Orientation.Horizontal, Background = ChipBrush, CornerRadius = new CornerRadius(5) };
        foreach (var decoration in Enum.GetValues<TextDecoration>())
        {
            var chip = Chip(Title(decoration), $"文字外观:{Title(decoration)}", state.Text.Decoration == decoration,
                new ToolbarCommand.TextDecorationChange(decoration));
            chip.CornerRadius = new CornerRadius(0);
            if (state.Text.Decoration != decoration)
            {
                chip.Background = Transparent;
            }

            segmented.Children.Add(chip);
        }

        _optionsRow.Children.Add(segmented);
        if (state.Text.Decoration == TextDecoration.Plain)
        {
            return;
        }

        // 色板改哪种颜色:点哪个,哪个高亮
        _optionsRow.Children.Add(ColorTarget(TextColorTarget.Foreground, "文字", state.Text.Foreground, state));
        _optionsRow.Children.Add(ColorTarget(TextColorTarget.Background, Title(state.Text.Decoration), state.Text.Background, state));
    }

    private Button ColorTarget(TextColorTarget target, string title, AnnotationColor color, ToolbarState state)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new Border
        {
            Width = 11, Height = 11, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(AnnotationRenderer.ToColor(color)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.5),
        });
        content.Children.Add(Label(title));
        var button = Bare(content, $"色板修改{title}颜色", new ToolbarCommand.TextColorTargetChange(target));
        button.Padding = new Thickness(6, 2, 6, 2);
        button.Background = state.TextColorTarget == target ? ChipOnBrush : ChipBrush;
        return button;
    }

    private Button Chip(string text, string help, bool on, ToolbarCommand command)
    {
        var button = Bare(Label(text), help, command);
        button.Padding = new Thickness(8, 2, 8, 2);
        button.CornerRadius = new CornerRadius(5);
        button.Background = on ? ChipOnBrush : ChipBrush;
        return button;
    }
}
