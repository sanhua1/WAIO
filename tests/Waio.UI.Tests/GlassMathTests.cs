using Waio.UI.Theming;

namespace Waio.UI.Tests;

public class GlassMathTests
{
    /// <summary>[R G B] 经过饱和度矩阵(输出 = 行向量 × M)。</summary>
    private static (double R, double G, double B) Apply(float[] m, double r, double g, double b) => (
        (r * m[0]) + (g * m[4]) + (b * m[8]),
        (r * m[1]) + (g * m[5]) + (b * m[9]),
        (r * m[2]) + (g * m[6]) + (b * m[10]));

    [Fact]
    public void 鲜艳度1_颜色不变()
    {
        var (r, g, b) = Apply(GlassMath.SaturationMatrix(1), 0.8, 0.3, 0.1);
        Assert.Equal(0.8, r, 5);
        Assert.Equal(0.3, g, 5);
        Assert.Equal(0.1, b, 5);
    }

    [Fact]
    public void 鲜艳度0_变成灰度()
    {
        var (r, g, b) = Apply(GlassMath.SaturationMatrix(0), 0.8, 0.3, 0.1);
        Assert.Equal(r, g, 5);
        Assert.Equal(g, b, 5);
    }

    [Fact]
    public void 鲜艳度大于1_颜色之间差距拉大_灰色不变()
    {
        var (r, _, b) = Apply(GlassMath.SaturationMatrix(1.8), 0.8, 0.3, 0.1);
        Assert.True(r - b > 0.8 - 0.1);
        var (gr, gg, gb) = Apply(GlassMath.SaturationMatrix(1.8), 0.5, 0.5, 0.5);
        Assert.Equal(0.5, gr, 5);
        Assert.Equal(0.5, gg, 5);
        Assert.Equal(0.5, gb, 5);
    }

    [Fact]
    public void 透明度不受影响()
    {
        var m = GlassMath.SaturationMatrix(2);
        Assert.Equal(1, m[15]);
        Assert.Equal(0, m[3]);
        Assert.Equal(0, m[16]);
    }

    [Fact]
    public void 窗口区域按显示器偏移和缩放换算()
    {
        var client = new GlassNative.Rect { Left = 2660, Top = 200, Right = 3060, Bottom = 600 };
        var monitor = new GlassNative.Rect { Left = 2560, Top = 0, Right = 5120, Bottom = 1440 };
        var region = GlassMath.SourceRegion(client, monitor, 0.25);
        Assert.Equal((25.0, 50.0, 100.0, 100.0), (region.X, region.Y, region.Width, region.Height));
    }

    [Fact]
    public void 读取范围向外扩3西格玛_并限制在截图内()
    {
        var padded = GlassMath.PaddedRegion(new Windows.Foundation.Rect(10, 10, 100, 50), sigma: 4, 640, 360);
        Assert.NotNull(padded);
        Assert.Equal(0, padded.Value.X);
        Assert.Equal(0, padded.Value.Y);
        Assert.Equal(124, padded.Value.Width);
        Assert.Equal(74, padded.Value.Height);
    }

    [Fact]
    public void 窗口完全在截图外_没有可读范围()
    {
        Assert.Null(GlassMath.PaddedRegion(new Windows.Foundation.Rect(-500, -500, 100, 100), sigma: 4, 640, 360));
    }
}
