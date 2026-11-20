using Waio.UI.Theming;
using Windows.UI;

namespace Waio.UI.Tests;

public class GlassThemeTests
{
    [Fact]
    public void 没有任何调整_用默认值()
    {
        Assert.Equal(GlassTheme.Default, new GlassOverrides().Resolve());
        Assert.False(new GlassOverrides().HasTweaks);
    }

    [Fact]
    public void 调整覆盖对应项_其余保持默认()
    {
        var theme = new GlassOverrides { BlurRadius = 30, Saturation = 1.2, Corners = GlassCorners.ExtraRounded }.Resolve();
        Assert.Equal(30, theme.BlurRadius);
        Assert.Equal(1.2, theme.Saturation);
        Assert.Equal(GlassCorners.ExtraRounded, theme.Corners);
        Assert.Equal(GlassTheme.Default.TintOpacity, theme.TintOpacity);
    }

    [Fact]
    public void 强调色写system_等同跟随系统()
    {
        Assert.Null(new GlassOverrides { Accent = GlassOverrides.SystemAccent }.Resolve().Accent);
        Assert.NotNull(new GlassOverrides { Accent = "#9B8CFF" }.Resolve().Accent);
    }

    [Fact]
    public void 写错的颜色_当作没写()
    {
        var theme = new GlassOverrides { Tint = "red", Accent = "#12" }.Resolve();
        Assert.Null(theme.Tint);
        Assert.Null(theme.Accent);
    }

    [Theory]
    [InlineData(0, GlassTheme.MinBlurRadius)]
    [InlineData(1000, GlassTheme.MaxBlurRadius)]
    [InlineData(double.NaN, 50)] // 非法值回到默认(2026-11-12 起默认 50)
    public void 模糊强度_收进合法范围(double input, double expected)
    {
        Assert.Equal(expected, new GlassOverrides { BlurRadius = input }.Resolve().BlurRadius);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, GlassTheme.MaxTintOpacity)]
    public void 色调浓度_不能到完全不透明(double input, double expected)
    {
        Assert.Equal(expected, new GlassOverrides { TintOpacity = input }.Resolve().TintOpacity);
    }

    [Theory]
    [InlineData(0, GlassTheme.MinSaturation)]
    [InlineData(9, GlassTheme.MaxSaturation)]
    public void 鲜艳度_收进合法范围(double input, double expected)
    {
        Assert.Equal(expected, new GlassOverrides { Saturation = input }.Resolve().Saturation);
    }

    [Fact]
    public void 默认色调随系统深浅色变_自定义色调两边都用()
    {
        Assert.Equal(GlassTheme.DefaultDarkTint, GlassTheme.Default.TintFor(dark: true));
        Assert.Equal(GlassTheme.DefaultLightTint, GlassTheme.Default.TintFor(dark: false));
        var red = Color.FromArgb(0xFF, 0xFF, 0, 0);
        Assert.Equal(red, (GlassTheme.Default with { Tint = red }).TintFor(dark: true));
    }

    [Theory]
    [InlineData(GlassCorners.Standard, 4, 8)]
    [InlineData(GlassCorners.Rounded, 6, 12)]
    [InlineData(GlassCorners.ExtraRounded, 8, 16)]
    public void 圆角档位(GlassCorners corners, double control, double card)
    {
        var theme = GlassTheme.Default with { Corners = corners };
        Assert.Equal(control, theme.ControlRadius);
        Assert.Equal(card, theme.CardRadius);
    }
}

public class GlassDefaultsTests
{
    [Fact]
    public void 默认圆角是标准档_控件4_卡片8()
    {
        Assert.Equal(GlassCorners.Standard, GlassTheme.Default.Corners);
        Assert.Equal(4, GlassTheme.Default.ControlRadius);
        Assert.Equal(8, GlassTheme.Default.CardRadius);
    }
}
