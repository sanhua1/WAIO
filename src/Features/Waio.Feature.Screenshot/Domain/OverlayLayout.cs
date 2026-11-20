namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 遮罩上各个浮动元素的位置(MAIO <c>OverlayLayout</c>,全局物理像素)。
/// MAIO 的距离单位是 pt,这里乘上所在显示器的缩放比例 <c>scale</c>。
/// </summary>
public static class OverlayLayout
{
    public const double GapDip = 8;
    public const double MagnifierOffsetDip = 20;

    /// <summary>工具栏:优先放在选区下方,放不下依次换到上方、选区内部(Flameshot 的规则)。右边和选区对齐。</summary>
    public static PxRect ToolbarFrame(int width, int height, PxRect selection, PxRect bounds, double scale)
    {
        var gap = (int)Math.Round(GapDip * scale);
        var x = Math.Min(Math.Max(selection.Right - width, bounds.X + gap), bounds.Right - width - gap);
        var below = selection.Bottom + gap;
        var above = selection.Y - gap - height;
        int y;
        if (below + height <= bounds.Bottom - gap)
        {
            y = below;
        }
        else if (above >= bounds.Y + gap)
        {
            y = above;
        }
        else
        {
            y = Math.Max(selection.Bottom - gap - height, bounds.Y + gap);
        }

        return new PxRect(x, y, width, height);
    }

    /// <summary>尺寸标签:放在选区左上角外侧,放不下时放到内侧。</summary>
    public static PxRect SizeLabelFrame(int width, int height, PxRect selection, PxRect bounds, double scale)
    {
        var spacing = (int)Math.Round(4 * scale);
        var outsideY = selection.Y - height - spacing;
        var y = outsideY >= bounds.Y ? outsideY : selection.Y + spacing;
        var x = Math.Min(Math.Max(selection.X, bounds.X), bounds.Right - width);
        return new PxRect(x, y, width, height);
    }

    /// <summary>放大镜:默认在鼠标右下方,靠近屏幕边缘时换到另一侧。</summary>
    public static PxRect MagnifierFrame(int width, int height, PxPoint cursor, PxRect bounds, double scale)
    {
        var offset = (int)Math.Round(MagnifierOffsetDip * scale);
        var x = cursor.X + offset;
        if (x + width > bounds.Right)
        {
            x = cursor.X - offset - width;
        }

        var y = cursor.Y + offset;
        if (y + height > bounds.Bottom)
        {
            y = cursor.Y - offset - height;
        }

        return new PxRect(Math.Max(x, bounds.X), Math.Max(y, bounds.Y), width, height);
    }
}

/// <summary>取色结果(sRGB,0~255)。</summary>
public readonly record struct SampledColor(byte Red, byte Green, byte Blue);

/// <summary>取色的显示和复制格式(MAIO ColorFormat)。</summary>
public enum ColorFormat
{
    Hex,
    Rgb,
}

public static class ColorFormats
{
    public static string Title(this ColorFormat format) => format == ColorFormat.Hex ? "HEX(#1A2B3C)" : "RGB(rgb(26, 43, 60))";

    public static ColorFormat Next(this ColorFormat format) => format == ColorFormat.Hex ? ColorFormat.Rgb : ColorFormat.Hex;

    public static string Format(this ColorFormat format, SampledColor color) => format == ColorFormat.Hex
        ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
        : $"rgb({color.Red}, {color.Green}, {color.Blue})";
}
