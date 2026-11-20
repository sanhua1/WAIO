namespace Waio.UI;

/// <summary>
/// Fluent 2 设计令牌(ADR-0002)。所有功能的 UI 用这里的数值,保证间距、圆角一致。
/// 颜色、字体不在这里定义:一律用系统主题资源(如 <c>TextFillColorPrimaryBrush</c>),这样自动跟随深浅色/高对比度/强调色以及外观设置。
/// 圆角以外观设置为准(ADR-0026):XAML 里用 <c>{ThemeResource WaioCardCornerRadius}</c> / <c>WaioControlCornerRadius</c>,代码里用 <c>Glass.Current.CardRadius</c>;下面两个常量只是"标准"档的值。
/// </summary>
public static class FluentTokens
{
    /// <summary>控件圆角(按钮、输入框)。</summary>
    public const double RadiusControl = 4;

    /// <summary>容器圆角(卡片、浮层、对话框)。</summary>
    public const double RadiusOverlay = 8;

    public const double Space4 = 4;
    public const double Space8 = 8;
    public const double Space12 = 12;
    public const double Space16 = 16;
    public const double Space24 = 24;
    public const double Space32 = 32;
}
