namespace Waio.Feature.Screenshot.Domain;

/// <summary>长截图时选区外的边框、右侧面板和预览绿框的位置(MAIO ScrollCaptureFrame / ScrollCaptureSidebar)。全局物理像素。</summary>
public static class ScrollCaptureLayout
{
    /// <summary>
    /// 右侧面板:放在选区右边,顶部对齐,一直延伸到工作区底部;右边放不下放左边,都放不下就放在选区里靠右(MAIO sidebarFrame)。
    /// </summary>
    public static PxRect Sidebar(PxRect selection, PxRect work, int width, int gap, int minimumHeight)
    {
        int x;
        if (selection.Right + gap + width <= work.Right)
        {
            x = selection.Right + gap;
        }
        else if (selection.X - gap - width >= work.X)
        {
            x = selection.X - gap - width;
        }
        else
        {
            x = Math.Min(selection.Right, work.Right) - width - gap;
        }

        var top = Math.Max(selection.Y, work.Y);
        var bottom = work.Bottom - gap;
        var height = Math.Max(minimumHeight, bottom - top);
        top = Math.Min(top, work.Bottom - height);
        return new PxRect(x, top, width, height);
    }

    /// <summary>选区外一圈 <paramref name="thickness"/> 宽的边框,拆成上、下、左、右四条(中间留空,不挡选区)。</summary>
    public static IReadOnlyList<PxRect> Border(PxRect selection, int thickness)
    {
        var outer = new PxRect(selection.X - thickness, selection.Y - thickness, selection.Width + thickness * 2, selection.Height + thickness * 2);
        return
        [
            outer with { Height = thickness },
            outer with { Y = selection.Bottom, Height = thickness },
            new PxRect(outer.X, selection.Y, thickness, selection.Height),
            new PxRect(selection.Right, selection.Y, thickness, selection.Height),
        ];
    }

    /// <summary>
    /// 贴着 <paramref name="area"/> 边缘、画在<b>里面</b>的一圈边框(全屏录屏,ADR-0030:屏幕外面没有地方画)。
    /// 上下两条占满宽度,左右两条夹在中间。边框窗口排除在截屏之外,不会录进视频。
    /// </summary>
    public static IReadOnlyList<PxRect> InnerBorder(PxRect area, int thickness) =>
    [
        area with { Height = thickness },
        area with { Y = area.Bottom - thickness, Height = thickness },
        new PxRect(area.X, area.Y + thickness, thickness, area.Height - thickness * 2),
        new PxRect(area.Right - thickness, area.Y + thickness, thickness, area.Height - thickness * 2),
    ];

    /// <summary>
    /// 预览上的绿框(当前画面在长图中的位置):<paramref name="previewHeight"/> 是预览显示的高度,返回绿框的上边和高度(同一单位)。
    /// </summary>
    public static (double Top, double Height) ViewportMarker(int canvasHeight, int frameHeight, int position, double previewHeight)
    {
        var total = Math.Max(1, canvasHeight);
        var height = Math.Min(previewHeight, frameHeight / (double)total * previewHeight);
        var top = Math.Min(previewHeight - height, Math.Max(0, position / (double)total * previewHeight));
        return (top, Math.Max(2, height));
    }
}
