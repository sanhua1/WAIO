using Waio.UI.Theming;
using Windows.UI;

namespace Waio.UI.Tests;

public class GlassPaletteTests
{
    private static readonly Color Purple = Color.FromArgb(0xFF, 0x9B, 0x8C, 0xFF);

    [Theory]
    [InlineData("#1A2B3C", 0xFF, 0x1A, 0x2B, 0x3C)]
    [InlineData("1a2b3c", 0xFF, 0x1A, 0x2B, 0x3C)]
    [InlineData("#801A2B3C", 0x80, 0x1A, 0x2B, 0x3C)]
    public void 解析颜色(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorText.TryParse(text, out var color));
        Assert.Equal(Color.FromArgb(a, r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void 不认识的颜色写法_解析失败(string? text)
    {
        Assert.False(ColorText.TryParse(text, out _));
    }

    [Fact]
    public void 格式化后能解析回来()
    {
        Assert.True(ColorText.TryParse(ColorText.Format(Purple), out var back));
        Assert.Equal(Purple, back);
    }

    [Fact]
    public void 强调色上的文字_按对比度选黑白()
    {
        Assert.True(ColorText.PrefersDarkText(Color.FromArgb(0xFF, 0xFF, 0xE0, 0x40))); // 亮黄
        Assert.False(ColorText.PrefersDarkText(Color.FromArgb(0xFF, 0x10, 0x20, 0x60))); // 深蓝
    }

    [Fact]
    public void 卡片浓度越高_卡片越实()
    {
        var thin = GlassPalette.Patched(GlassTheme.Default with { SurfaceOpacity = 0.2 }, dark: false);
        var thick = GlassPalette.Patched(GlassTheme.Default with { SurfaceOpacity = 0.9 }, dark: false);
        Assert.True(thick["CardBackgroundFillColorDefaultBrush"].A > thin["CardBackgroundFillColorDefaultBrush"].A);
    }

    [Fact]
    public void 深色下默认白色卡片层很淡()
    {
        var card = GlassPalette.Patched(GlassTheme.Default, dark: true)["CardBackgroundFillColorDefaultBrush"];
        Assert.True(card.A < 0x30, $"alpha={card.A}");
    }

    [Fact]
    public void 没有自定义强调色_不改强调色画刷()
    {
        var map = GlassPalette.Patched(GlassTheme.Default, dark: false);
        Assert.DoesNotContain(GlassPalette.AccentKeys, map.ContainsKey);
    }

    [Fact]
    public void 自定义强调色_改全部强调色画刷_填充色就是用户选的颜色()
    {
        var map = GlassPalette.Patched(GlassTheme.Default with { Accent = Purple }, dark: true);
        Assert.All(GlassPalette.AccentKeys, key => Assert.True(map.ContainsKey(key), key));
        Assert.Equal(Purple, map["AccentFillColorDefaultBrush"]);
    }

    [Fact]
    public void 导航栏透明_选中项是强调色淡胶囊_没有竖条()
    {
        var owned = GlassPalette.Owned(GlassTheme.Default, dark: false, Purple);
        Assert.Equal(0, owned["NavigationViewContentBackground"].A);
        Assert.Equal(0, owned["NavigationViewSelectionIndicatorForeground"].A);
        var selected = owned["NavigationViewItemBackgroundSelected"];
        Assert.Equal((Purple.R, Purple.G, Purple.B), (selected.R, selected.G, selected.B));
        Assert.InRange(selected.A, 1, 0x80);
    }
}
