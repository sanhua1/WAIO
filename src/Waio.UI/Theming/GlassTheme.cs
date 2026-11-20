using Windows.UI;

namespace Waio.UI.Theming;

/// <summary>圆角档位(ADR-0026)。窗口本身的圆角由系统决定(8 px),这里管窗口里的卡片和控件。</summary>
public enum GlassCorners
{
    /// <summary>控件 4 / 卡片 8:和 Windows 11 一样。固定用这一档(去掉圆角设置,其他档不好看)。</summary>
    Standard,

    /// <summary>控件 6 / 卡片 12。</summary>
    Rounded,

    /// <summary>控件 8 / 卡片 16。</summary>
    ExtraRounded,
}

/// <summary>
/// 一套完整的玻璃参数(ADR-0027)。没有预设、没有深浅色选项:深浅色永远跟随系统,
/// 用户只调玻璃本身。所有值都已收进合法范围,可直接用。
/// </summary>
/// <param name="BlurRadius">模糊强度(物理像素,约等于 2σ)。越大背后越朦胧。</param>
/// <param name="Saturation">鲜艳度:1 = 原样,&gt; 1 让透上来的颜色更鲜艳(macOS 毛玻璃的关键)。</param>
/// <param name="Tint">色调;null = 按深浅色取默认的浅灰 / 深灰。</param>
/// <param name="TintOpacity">色调浓度 0–0.95:越大越接近纯色。</param>
/// <param name="Surface">卡片底色;null = 白色(浅色)/ 白色淡层(深色)。</param>
/// <param name="SurfaceOpacity">卡片浓度 0–1。</param>
/// <param name="Accent">强调色;null = 跟随系统。</param>
public sealed record GlassTheme(
    double BlurRadius,
    double Saturation,
    Color? Tint,
    double TintOpacity,
    Color? Surface,
    double SurfaceOpacity,
    Color? Accent,
    GlassCorners Corners)
{
    public const double MinBlurRadius = 12;
    public const double MaxBlurRadius = 120;
    public const double MinSaturation = 0.5;
    public const double MaxSaturation = 2.5;

    /// <summary>色调浓度上限:到 1 就完全看不到背后了,没有玻璃的意义。</summary>
    public const double MaxTintOpacity = 0.95;

    /// <summary>
    /// 默认值(实际调过效果后定下的)——
    /// 模糊 50、鲜艳度 1.2、色调浓度 0.75、卡片浓度 0.3;色调 / 卡片底色 / 强调色仍跟随深浅色和系统。
    /// (最初参照 macOS 毛玻璃是 60 / 1.8 / 0.55 / 0.45。)
    /// </summary>
    public static readonly GlassTheme Default = new(
        BlurRadius: 50, Saturation: 1.2, Tint: null, TintOpacity: 0.75,
        Surface: null, SurfaceOpacity: 0.3, Accent: null, GlassCorners.Standard);

    /// <summary>默认色调(浅色主题)。</summary>
    public static readonly Color DefaultLightTint = Color.FromArgb(0xFF, 0xF4, 0xF5, 0xF8);

    /// <summary>默认色调(深色主题)。</summary>
    public static readonly Color DefaultDarkTint = Color.FromArgb(0xFF, 0x1E, 0x1F, 0x24);

    /// <summary>实际生效的色调。</summary>
    public Color TintFor(bool dark) => Tint ?? (dark ? DefaultDarkTint : DefaultLightTint);

    /// <summary>把各项收进合法范围(配置文件是不可信输入)。</summary>
    public GlassTheme Clamped() => this with
    {
        BlurRadius = Clamp(BlurRadius, MinBlurRadius, MaxBlurRadius, Default.BlurRadius),
        Saturation = Clamp(Saturation, MinSaturation, MaxSaturation, Default.Saturation),
        TintOpacity = Clamp(TintOpacity, 0, MaxTintOpacity, Default.TintOpacity),
        SurfaceOpacity = Clamp(SurfaceOpacity, 0, 1, Default.SurfaceOpacity),
    };

    /// <summary>卡片圆角。</summary>
    public double CardRadius => Corners switch
    {
        GlassCorners.Standard => 8,
        GlassCorners.ExtraRounded => 16,
        _ => 12,
    };

    /// <summary>控件(按钮、输入框)圆角。</summary>
    public double ControlRadius => Corners switch
    {
        GlassCorners.Standard => 4,
        GlassCorners.ExtraRounded => 8,
        _ => 6,
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// 用户在默认值之上的调整(存进 app.json)。每一项 null = 用默认值。
/// 颜色用 "#RRGGBB" 字符串;<see cref="Accent"/> 写 "system" 等同于 null(跟随系统)。
/// </summary>
public sealed record GlassOverrides
{
    public double? BlurRadius { get; init; }

    public double? Saturation { get; init; }

    public string? Tint { get; init; }

    public double? TintOpacity { get; init; }

    public string? Surface { get; init; }

    public double? SurfaceOpacity { get; init; }

    public string? Accent { get; init; }

    public GlassCorners? Corners { get; init; }

    /// <summary>"跟随系统强调色"的写法。</summary>
    public const string SystemAccent = "system";

    /// <summary>有没有任何调整。</summary>
    public bool HasTweaks => this != new GlassOverrides();

    /// <summary>默认值 + 调整 → 完整参数。写错的颜色当作没写。</summary>
    public GlassTheme Resolve()
    {
        var d = GlassTheme.Default;
        return new GlassTheme(
            BlurRadius ?? d.BlurRadius,
            Saturation ?? d.Saturation,
            ColorText.TryParse(Tint, out var tint) ? tint : d.Tint,
            TintOpacity ?? d.TintOpacity,
            ColorText.TryParse(Surface, out var surface) ? surface : d.Surface,
            SurfaceOpacity ?? d.SurfaceOpacity,
            Accent != SystemAccent && ColorText.TryParse(Accent, out var accent) ? accent : d.Accent,
            Corners ?? d.Corners).Clamped();
    }
}
