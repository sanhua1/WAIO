using Waio.Feature.Launcher.Panel;
using Waio.UI;

namespace Waio.Feature.Launcher.Infrastructure;

/// <summary><see cref="IPanelSystem"/> 的真实 Windows 实现(包装 <see cref="WindowChrome"/> / <see cref="ScreenLocator"/>)。</summary>
internal sealed class WindowsPanelSystem : IPanelSystem
{
    /// <summary>等原窗口重新成为前台的最长时间(同剪贴板面板)。</summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMilliseconds(600);

    public nint Foreground() => WindowChrome.Foreground();

    public Task RestoreAsync(nint window) => WindowChrome.RestoreForegroundAsync(window, RestoreTimeout);

    public void AllowForegroundHandoff() => WindowChrome.AllowForegroundHandoff();

    /// <summary>出现在鼠标所在的显示器上(MAIO:用户当前在操作的那块屏幕)。</summary>
    public PanelPlacement Locate()
    {
        var monitor = ScreenLocator.MonitorAt(ScreenLocator.CursorPosition());
        return new PanelPlacement(monitor.WorkArea, monitor.Scale);
    }
}
