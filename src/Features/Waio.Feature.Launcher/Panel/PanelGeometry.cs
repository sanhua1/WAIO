using Waio.UI;

namespace Waio.Feature.Launcher.Panel;

/// <summary>
/// 面板大小和位置:按当前显示器工作区的百分比算目标大小,再用逻辑像素的上下限夹住。
/// <para>
/// 只按百分比:比例总是对的,但能放下多少内容取决于逻辑像素(字号按逻辑像素算)——
/// 1080p@150% 的 40% 只有 512 逻辑像素宽,列表 + 预览放不下;4K@100% 的 40% 有 1536,一行拉得太长。
/// 所以夹在 [<see cref="MinWidthDip"/>, <see cref="MaxWidthDip"/>] 之间,最后不超过工作区的 <see cref="MaxScreenFraction"/>。
/// 4K@150% 上 40% × 40% 正好落在范围内,就是精确的 40%。
/// </para>
/// </summary>
internal static class PanelGeometry
{
    public const int DefaultWidthPercent = 40;
    public const int DefaultHeightPercent = 40;
    public const int MinPercent = 20;
    public const int MaxPercent = 90;

    /// <summary>列表 + 预览都放得下的最小宽度。</summary>
    public const double MinWidthDip = 760;
    public const double MaxWidthDip = 1400;
    public const double MinHeightDip = 420;
    public const double MaxHeightDip = 1000;

    /// <summary>无论怎么算都不超过工作区的这个比例(很小的屏幕上不溢出)。</summary>
    public const double MaxScreenFraction = 0.95;

    /// <summary>比这窄就收起预览,只留列表(极小的屏幕)。</summary>
    public const double PreviewMinWidthDip = 720;

    /// <summary>搜索框所在行的中线放在工作区高度的这个比例处(从上往下;MAIO 用黄金分割)。</summary>
    public const double SearchLineRatio = 0.382;

    public static int ClampPercent(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>面板在屏幕上的位置和大小(物理像素)。<paramref name="searchRowDip"/>:搜索框那一行的高度。</summary>
    public static ScreenRect Bounds(PanelPlacement placement, double searchRowDip)
    {
        var work = placement.WorkArea;
        var scale = placement.Scale > 0 ? placement.Scale : 1;
        var width = Side(work.Width, ClampPercent(placement.WidthPercent), MinWidthDip, MaxWidthDip, scale);
        var height = Side(work.Height, ClampPercent(placement.HeightPercent), MinHeightDip, MaxHeightDip, scale);

        var x = work.Left + ((work.Width - width) / 2);
        var y = (int)(work.Top + (work.Height * SearchLineRatio) - (searchRowDip * scale / 2));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new ScreenRect(x, y, x + width, y + height);
    }

    /// <summary>宽度(逻辑像素)够不够同时放列表和预览。</summary>
    public static bool ShowsPreview(double widthDip) => widthDip >= PreviewMinWidthDip;

    private static int Side(int workPixels, int percent, double minDip, double maxDip, double scale)
    {
        var dip = workPixels * percent / 100.0 / scale;
        dip = Math.Clamp(dip, minDip, maxDip);
        var pixels = (int)Math.Round(dip * scale);
        return Math.Min(pixels, (int)(workPixels * MaxScreenFraction));
    }
}
