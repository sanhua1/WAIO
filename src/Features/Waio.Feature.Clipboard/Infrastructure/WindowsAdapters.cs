using Waio.Feature.Clipboard.Panel;
using Waio.UI;

namespace Waio.Feature.Clipboard.Infrastructure;

/// <summary>前台窗口:真实 Windows 实现(包装 <see cref="ScreenLocator"/> / <see cref="WindowChrome"/>)。</summary>
internal sealed class WindowsWindowFocus : IWindowFocus
{
    /// <summary>等原窗口重新成为前台的最长时间(MAIO 用 600 ms)。</summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMilliseconds(600);

    public nint Foreground() => ScreenLocator.ForegroundWindow();

    public Task<bool> RestoreAsync(nint window) => WindowChrome.RestoreForegroundAsync(window, RestoreTimeout);
}

/// <summary>按设置决定面板弹在哪:文字光标处 / 鼠标处 / 屏幕中央。</summary>
internal sealed class ScreenPlacement : IPlacementProvider
{
    /// <summary>取文字光标最多等这么久;超时按"拿不到光标"处理,不让面板迟迟不出来。</summary>
    private static readonly TimeSpan CaretTimeout = TimeSpan.FromMilliseconds(250);

    public async Task<PanelPlacement> LocateAsync(nint foreground, ClipboardSettings settings)
    {
        var cursor = ScreenLocator.CursorPosition();
        var cursorRect = new ScreenRect(cursor.X, cursor.Y, cursor.X, cursor.Y);

        ScreenRect? anchor = settings.PopupPosition switch
        {
            PopupPosition.Caret => await ScreenLocator.CaretRectAsync(foreground, CaretTimeout)
                ?? (settings.CaretFallback == CaretFallback.Mouse ? cursorRect : null),
            PopupPosition.Mouse => cursorRect,
            _ => null,
        };

        // 贴着光标/鼠标时用它所在的显示器;居中时用鼠标所在的显示器(用户当前在操作的那块)
        var reference = anchor is { } a ? new ScreenPoint(a.Left, a.Top) : cursor;
        var monitor = ScreenLocator.MonitorAt(reference);
        return new PanelPlacement(anchor, monitor.WorkArea, monitor.Scale);
    }
}
