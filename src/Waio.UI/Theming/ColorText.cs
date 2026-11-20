using System.Globalization;
using Windows.UI;

namespace Waio.UI.Theming;

/// <summary>颜色的文字形式("#RRGGBB" / "#AARRGGBB")和简单的颜色运算。</summary>
public static class ColorText
{
    /// <summary>解析 "#RRGGBB" 或 "#AARRGGBB"(# 可省略)。其他写法一律失败。</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is null || (hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var a = hex.Length == 8 ? (byte)(value >> 24) : (byte)0xFF;
        color = Color.FromArgb(a, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>"#RRGGBB"(不带透明度)。</summary>
    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>同一颜色换透明度。</summary>
    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>往白色(amount &gt; 0)或黑色(amount &lt; 0)混合,amount ∈ [-1, 1]。</summary>
    public static Color Mix(Color color, double amount)
    {
        var target = amount >= 0 ? 255.0 : 0.0;
        var t = Math.Clamp(Math.Abs(amount), 0, 1);
        byte Channel(byte c) => (byte)Math.Round(c + ((target - c) * t));
        return Color.FromArgb(color.A, Channel(color.R), Channel(color.G), Channel(color.B));
    }

    /// <summary>相对亮度(WCAG 定义),0 = 黑,1 = 白。</summary>
    public static double Luminance(Color color)
    {
        static double Linear(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }

    /// <summary>放在这个颜色上的文字该用黑字还是白字(取对比度更高的那个)。</summary>
    public static bool PrefersDarkText(Color background)
    {
        var l = Luminance(background);
        var withBlack = (l + 0.05) / 0.05;
        var withWhite = 1.05 / (l + 0.05);
        return withBlack > withWhite;
    }
}
