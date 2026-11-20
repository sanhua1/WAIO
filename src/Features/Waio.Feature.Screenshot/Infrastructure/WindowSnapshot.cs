using System.Runtime.InteropServices;
using System.Text;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 冻结时读取屏幕上的顶层窗口(MAIO WindowSnapshot 用 CGWindowListCopyWindowInfo,约 1 ms)。
/// <c>EnumWindows</c> 按 Z 序从上到下返回;跳过隐藏、最小化、被 DWM 隐藏(cloaked,如其他虚拟桌面的窗口、挂起的 UWP)的窗口。
/// 窗口边界用 DWM 的扩展边框(<c>DwmGetWindowAttribute</c>,不含 Windows 10/11 的透明阴影)。
/// </summary>
internal static class WindowSnapshot
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExLayered = 0x80000;
    private const uint LwaAlpha = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    public static IReadOnlyList<WindowInfo> Current()
    {
        var result = new List<WindowInfo>();
        var name = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (Describe(hwnd, name) is { } info)
            {
                result.Add(info);
            }

            return true;
        }, 0);
        return result;
    }

    /// <summary>单个窗口的信息;不可见的返回 null。</summary>
    public static WindowInfo? Describe(nint hwnd, StringBuilder? name = null)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)
            || DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out ScreenCapturer.NativeRect r, Marshal.SizeOf<ScreenCapturer.NativeRect>()) != 0
            && !ScreenCapturer.GetWindowRect(hwnd, out r))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var exStyle = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        var alpha = 1.0;
        if ((exStyle & WsExLayered) != 0 && GetLayeredWindowAttributes(hwnd, out _, out var value, out var flags) && (flags & LwaAlpha) != 0)
        {
            alpha = value / 255.0;
        }

        name ??= new StringBuilder(256);
        name.Clear();
        _ = GetClassNameW(hwnd, name, name.Capacity);
        return new WindowInfo(hwnd, (int)pid, PxRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom), name.ToString(), alpha,
            (exStyle & WsExTransparent) != 0, GetWindowDisplayAffinity(hwnd, out var affinity) && affinity != 0);
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out ScreenCapturer.NativeRect value, int size);
}
