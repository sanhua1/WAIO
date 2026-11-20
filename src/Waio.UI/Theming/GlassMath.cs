using Windows.Foundation;

namespace Waio.UI.Theming;

/// <summary>自绘玻璃的纯计算部分(可单测,ADR-0027)。</summary>
internal static class GlassMath
{
    /// <summary>
    /// 饱和度矩阵(Win2D <c>Matrix5x4</c> 的 20 个元素,行优先:M11..M14, M21..M24, …, M51..M54)。
    /// 输出 = [R G B A 1] × M;s = 1 原样,s &gt; 1 更鲜艳,s = 0 灰度。亮度权重用 Rec.709。
    /// </summary>
    public static float[] SaturationMatrix(double s)
    {
        var r = (float)((1 - s) * 0.2126);
        var g = (float)((1 - s) * 0.7152);
        var b = (float)((1 - s) * 0.0722);
        var k = (float)s;
        return
        [
            r + k, r, r, 0,
            g, g + k, g, 0,
            b, b, b + k, 0,
            0, 0, 0, 1,
            0, 0, 0, 0,
        ];
    }

    /// <summary>
    /// 窗口客户区对应到缩小后的显示器截图上的区域。
    /// </summary>
    /// <param name="client">客户区在屏幕上的位置(物理像素)。</param>
    /// <param name="monitor">显示器在屏幕上的位置(物理像素)。</param>
    /// <param name="scale">截图相对显示器的缩放(如 0.25)。</param>
    public static Rect SourceRegion(GlassNative.Rect client, GlassNative.Rect monitor, double scale) => new(
        (client.Left - monitor.Left) * scale,
        (client.Top - monitor.Top) * scale,
        Math.Max(0, client.Width * scale),
        Math.Max(0, client.Height * scale));

    /// <summary>
    /// 模糊要读的范围:目标区域向外扩 3σ(高斯核的有效半径),再限制在截图内。
    /// 扩出去的部分让边缘也能模糊到真实的邻近像素,而不是一圈发黑。返回 null 表示和截图没有交集。
    /// </summary>
    public static Rect? PaddedRegion(Rect region, double sigma, double imageWidth, double imageHeight)
    {
        var pad = (3 * sigma) + 2;
        var left = Math.Max(0, region.X - pad);
        var top = Math.Max(0, region.Y - pad);
        var right = Math.Min(imageWidth, region.X + region.Width + pad);
        var bottom = Math.Min(imageHeight, region.Y + region.Height + pad);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : null;
    }
}
