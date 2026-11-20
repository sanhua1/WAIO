using Waio.UI;

namespace Waio.Feature.Clipboard.Domain;

/// <summary>剪贴板面板放在哪(纯函数,可单测;参考 MAIO 的 PopupPlacement)。</summary>
internal static class PopupPlacement
{
    /// <summary>面板和光标所在行之间留的空隙。</summary>
    public const int Gap = 6;

    /// <summary>面板内容的左内边距:左移这么多,列表文字正好和光标对齐。</summary>
    public const int ContentInset = 12;

    private const int EdgeMargin = 4;

    /// <summary>
    /// 放在 <paramref name="anchor"/>(光标所在那一行,或鼠标处)的正下方;下方放不下就放上方,不挡住这一行。
    /// 返回面板左上角。<paramref name="visible"/> 是所在显示器的工作区。
    /// </summary>
    public static ScreenPoint TopLeft(ScreenRect anchor, int width, int height, ScreenRect visible)
    {
        var y = anchor.Bottom + Gap;
        if (y + height > visible.Bottom)
        {
            y = anchor.Top - Gap - height;
        }

        var maxX = Math.Max(visible.Left + EdgeMargin, visible.Right - width - EdgeMargin);
        var x = Math.Clamp(anchor.Left - ContentInset, visible.Left + EdgeMargin, maxX);
        y = Math.Clamp(y, visible.Top, Math.Max(visible.Top, visible.Bottom - height));
        return new ScreenPoint(x, y);
    }

    /// <summary>显示器工作区正中间。</summary>
    public static ScreenPoint Centered(int width, int height, ScreenRect visible) =>
        new(visible.Left + (visible.Width - width) / 2, visible.Top + (visible.Height - height) / 2);
}

