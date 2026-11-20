using Windows.UI;

namespace Waio.UI.Theming;

/// <summary>
/// 由外观参数算出要改的画刷颜色(纯计算,可单测;ADR-0026)。
/// <para>
/// 两类键:
/// <list type="bullet">
/// <item><see cref="Patched"/>:Fluent 自带的画刷(卡片、分隔线、强调色等)。WinUI 控件模板里大量用
/// <c>StaticResource</c> 别名指向同一个画刷对象(如 ToggleSwitchFillOn → AccentFillColorDefaultBrush),
/// 所以改的是<b>原画刷对象的颜色</b>,别名跟着变,不必逐个覆盖。</item>
/// <item><see cref="Owned"/>:WAIO 自己放在应用级主题字典里的画刷(导航栏透明、选中胶囊等)。
/// 模板对这些键用的是 <c>ThemeResource</c>,应用级的值优先。</item>
/// </list>
/// </para>
/// </summary>
public static class GlassPalette
{
    /// <summary>跟随强调色变化的 Fluent 键(没有自定义强调色时恢复原值)。</summary>
    public static readonly string[] AccentKeys =
    [
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush",
        "AccentFillColorSelectedTextBackgroundBrush",
    ];

    /// <summary>Fluent 自带、按外观参数改颜色的键。</summary>
    public static IReadOnlyDictionary<string, Color> Patched(GlassTheme theme, bool dark)
    {
        var surface = theme.Surface ?? Colors.White;

        // 深色主题下白色卡片层要很淡(Fluent 原值约 5%);自定义底色时按色块本身的浓度来
        var alpha = theme.SurfaceOpacity * (theme.Surface is null && dark ? 0.2 : dark ? 0.6 : 1.0);
        var map = new Dictionary<string, Color>
        {
            ["CardBackgroundFillColorDefaultBrush"] = ColorText.WithAlpha(surface, alpha),
            ["CardBackgroundFillColorSecondaryBrush"] = ColorText.WithAlpha(surface, alpha * 0.6),
            ["LayerFillColorDefaultBrush"] = ColorText.WithAlpha(surface, alpha * 0.5),
            ["LayerOnAcrylicFillColorDefaultBrush"] = ColorText.WithAlpha(surface, alpha * 0.5),

            // 玻璃边缘:深色下一圈淡白高光,浅色下极淡的灰
            ["CardStrokeColorDefaultBrush"] = dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0),
            ["DividerStrokeColorDefaultBrush"] = dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0, 0, 0),
        };

        if (theme.Accent is { } accent)
        {
            foreach (var (key, color) in AccentColors(accent, dark))
            {
                map[key] = color;
            }
        }

        return map;
    }

    /// <summary>WAIO 自己的画刷(应用级主题字典)。<paramref name="accent"/> 是实际生效的强调色(自定义或系统)。</summary>
    public static IReadOnlyDictionary<string, Color> Owned(GlassTheme theme, bool dark, Color accent)
    {
        var transparent = Colors.Transparent;
        var hover = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0, 0, 0);
        return new Dictionary<string, Color>
        {
            // 设置窗口:导航栏和内容区不再各铺一层底色,直接落在玻璃上
            ["NavigationViewContentBackground"] = transparent,
            ["NavigationViewContentGridBorderBrush"] = transparent,
            ["NavigationViewExpandedPaneBackground"] = transparent,
            ["NavigationViewDefaultPaneBackground"] = transparent,

            // 选中项:强调色淡胶囊,不要左侧竖条
            ["NavigationViewSelectionIndicatorForeground"] = transparent,
            ["NavigationViewItemBackgroundSelected"] = ColorText.WithAlpha(accent, dark ? 0.24 : 0.16),
            ["NavigationViewItemBackgroundSelectedPointerOver"] = ColorText.WithAlpha(accent, dark ? 0.30 : 0.22),
            ["NavigationViewItemBackgroundSelectedPressed"] = ColorText.WithAlpha(accent, dark ? 0.18 : 0.12),
            ["NavigationViewItemBackgroundPointerOver"] = hover,

            ["WaioAccentSoftBrush"] = ColorText.WithAlpha(accent, dark ? 0.24 : 0.16),
            ["WaioGlassEdgeBrush"] = dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
        };
    }

    /// <summary>
    /// 自定义强调色的各级颜色。填充色就用用户选的颜色本身(用户挑的就是想看到的);
    /// 文字用的强调色在深色下提亮、浅色下压暗,保证在玻璃上读得清;强调色上面的文字按对比度选黑或白。
    /// </summary>
    public static IEnumerable<(string Key, Color Color)> AccentColors(Color accent, bool dark)
    {
        accent = ColorText.WithAlpha(accent, 1);
        var text = ColorText.Mix(accent, dark ? 0.35 : -0.3);
        var onAccent = ColorText.PrefersDarkText(accent) ? Colors.Black : Colors.White;
        yield return ("AccentFillColorDefaultBrush", accent);
        yield return ("AccentFillColorSecondaryBrush", ColorText.WithAlpha(accent, 0.9));
        yield return ("AccentFillColorTertiaryBrush", ColorText.WithAlpha(accent, 0.8));
        yield return ("AccentTextFillColorPrimaryBrush", text);
        yield return ("AccentTextFillColorSecondaryBrush", text);
        yield return ("AccentTextFillColorTertiaryBrush", ColorText.Mix(text, dark ? -0.1 : 0.1));
        yield return ("TextOnAccentFillColorPrimaryBrush", onAccent);
        yield return ("TextOnAccentFillColorSecondaryBrush", ColorText.WithAlpha(onAccent, 0.7));
        yield return ("AccentFillColorSelectedTextBackgroundBrush", accent);
    }

    private static class Colors
    {
        public static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Color Black = Color.FromArgb(0xFF, 0, 0, 0);
        public static readonly Color Transparent = Color.FromArgb(0, 0xFF, 0xFF, 0xFF);
    }
}
