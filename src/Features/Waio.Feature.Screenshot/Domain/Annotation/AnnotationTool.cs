namespace Waio.Feature.Screenshot.Domain.Annotation;

/// <summary>十种标注工具(MAIO AnnotationTool,spec 4.2)。</summary>
public enum AnnotationTool
{
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Highlighter,
    Text,
    Mosaic,
    Blur,
    Counter,
}

public static class AnnotationTools
{
    public static readonly AnnotationTool[] All = Enum.GetValues<AnnotationTool>();

    public static string Title(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => "矩形",
        AnnotationTool.Ellipse => "椭圆",
        AnnotationTool.Arrow => "箭头",
        AnnotationTool.Line => "直线",
        AnnotationTool.Pen => "画笔",
        AnnotationTool.Highlighter => "荧光笔",
        AnnotationTool.Text => "文字",
        AnnotationTool.Mosaic => "马赛克",
        AnnotationTool.Blur => "模糊",
        _ => "序号",
    };

    /// <summary>截图时选择工具的按键(R O A L P H T M B N)。</summary>
    public static char Key(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => 'R',
        AnnotationTool.Ellipse => 'O',
        AnnotationTool.Arrow => 'A',
        AnnotationTool.Line => 'L',
        AnnotationTool.Pen => 'P',
        AnnotationTool.Highlighter => 'H',
        AnnotationTool.Text => 'T',
        AnnotationTool.Mosaic => 'M',
        AnnotationTool.Blur => 'B',
        _ => 'N',
    };

    public static AnnotationTool? ForKey(char key) =>
        All.Cast<AnnotationTool?>().FirstOrDefault(t => t!.Value.Key() == char.ToUpperInvariant(key));

    /// <summary>大小的范围(连续可调,整数):线宽、字号、马赛克块大小、模糊强度或序号半径,单位 pt。</summary>
    public static (double Min, double Max) SizeRange(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlighter => (4, 80),
        AnnotationTool.Text => (8, 160),
        AnnotationTool.Mosaic => (2, 60),
        AnnotationTool.Blur => (1, 50),
        AnnotationTool.Counter => (6, 60),
        _ => (1, 40),
    };

    public static double DefaultSize(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlighter => 16,
        AnnotationTool.Text => 18,
        AnnotationTool.Mosaic => 8,
        AnnotationTool.Blur => 8,
        AnnotationTool.Counter => 13,
        _ => 4,
    };

    /// <summary>限制在范围内并取整。</summary>
    public static double ClampedSize(this AnnotationTool tool, double value)
    {
        var (min, max) = tool.SizeRange();
        return Math.Min(Math.Max(Math.Round(value, MidpointRounding.AwayFromZero), min), max);
    }

    /// <summary>是否使用颜色(马赛克、模糊不用)。</summary>
    public static bool UsesColor(this AnnotationTool tool) => tool is not (AnnotationTool.Mosaic or AnnotationTool.Blur);

    /// <summary>有第三行专属选项(实心、圆角、虚线、文字外观、下一个序号)。</summary>
    public static bool HasOptions(this AnnotationTool tool) =>
        tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Line or AnnotationTool.Text or AnnotationTool.Counter;
}

/// <summary>文字工具里,色板改的是文字颜色还是背景色(描边 / 底色)。</summary>
public enum TextColorTarget
{
    Foreground,
    Background,
}

/// <summary>记住的工具样式(MAIO ToolStyle;保存在设置里,"上次用过的颜色和粗细会记住")。大小单位是 pt。</summary>
public sealed class ToolStyle
{
    public AnnotationColor Color { get; set; } = AnnotationColors.Red;

    /// <summary>荧光笔单独记颜色,默认黄色。</summary>
    public AnnotationColor HighlighterColor { get; set; } = AnnotationColors.Yellow;

    /// <summary>每个工具的大小(pt),键是工具名(小写开头)。</summary>
    public Dictionary<string, double> Sizes { get; set; } = [];

    public bool Filled { get; set; }

    public bool Rounded { get; set; }

    public bool Dashed { get; set; }

    public bool Bold { get; set; }

    public TextDecoration TextDecoration { get; set; } = TextDecoration.Plain;

    /// <summary>文字描边 / 底色的颜色。</summary>
    public AnnotationColor TextBackgroundColor { get; set; } = AnnotationColors.White;

    public double Size(AnnotationTool tool) => tool.ClampedSize(Sizes.TryGetValue(Name(tool), out var value) ? value : tool.DefaultSize());

    public void SetSize(double value, AnnotationTool tool) => Sizes[Name(tool)] = tool.ClampedSize(value);

    public AnnotationColor ColorFor(AnnotationTool tool) => tool == AnnotationTool.Highlighter ? HighlighterColor : Color;

    public void SetColor(AnnotationColor value, AnnotationTool? tool)
    {
        if (tool == AnnotationTool.Highlighter)
        {
            HighlighterColor = value;
        }
        else
        {
            Color = value;
        }
    }

    public ToolStyle Clone()
    {
        var copy = (ToolStyle)MemberwiseClone();
        copy.Sizes = new Dictionary<string, double>(Sizes);
        return copy;
    }

    private static string Name(AnnotationTool tool) => char.ToLowerInvariant(tool.ToString()[0]) + tool.ToString()[1..];
}
