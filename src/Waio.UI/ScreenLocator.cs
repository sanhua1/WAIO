using System.Runtime.InteropServices;

namespace Waio.UI;

/// <summary>屏幕坐标下的矩形(Windows:原点在左上,y 向下;单位物理像素)。</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

public readonly record struct ScreenPoint(int X, int Y);

/// <param name="WorkArea">显示器工作区(去掉任务栏)。</param>
/// <param name="Scale">该显示器的缩放比例,100% = 1.0。</param>
public readonly record struct MonitorInfo(ScreenRect WorkArea, double Scale);

/// <summary>
/// 定位屏幕上的东西:文字光标、鼠标、显示器。弹出式面板(剪贴板、启动台、翻译)共用。
/// 要求进程是 Per-Monitor V2 DPI 感知(见 app.manifest),这样拿到的都是物理像素。
/// </summary>
public static class ScreenLocator
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    public static nint ForegroundWindow() => GetForegroundWindow();

    public static ScreenPoint CursorPosition() =>
        GetCursorPos(out var p) ? new ScreenPoint(p.X, p.Y) : new ScreenPoint(0, 0);

    /// <summary>
    /// <see cref="CaretRect"/> 的异步版:在后台线程上做(会跨进程调用目标程序),超时就当拿不到。
    /// 目标程序卡住时 UI Automation 可能等很久,不能让弹出面板跟着卡。
    /// </summary>
    public static async Task<ScreenRect?> CaretRectAsync(nint foregroundWindow, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => CaretRect(foregroundWindow)).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// 窗口 <paramref name="window"/> 里鼠标处最深的控件(UI Automation,截图的"识别控件")。在后台线程上做;
    /// 超过 <paramref name="timeout"/> 返回已经找到的那一层(可能就是窗口自己),出错返回 null。
    /// </summary>
    public static Task<ScreenRect?> ControlRectAsync(nint window, ScreenPoint point, TimeSpan timeout) =>
        Task.Run(() => UiaCaret.ElementRectAt(window, point, timeout));

    /// <summary>
    /// 焦点处<b>选中的文字</b>(UI Automation TextPattern,不碰剪贴板);在后台线程上做,超时或拿不到返回 null。
    /// 浏览器、Electron 等很多程序不提供,调用方应退回"模拟 Ctrl+C"(翻译功能的做法,参考 MAIO SelectionReader)。
    /// </summary>
    public static async Task<string?> SelectedTextAsync(TimeSpan timeout)
    {
        try
        {
            return await Task.Run(UiaCaret.SelectedText).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// 前台窗口里文字光标(插入符)的屏幕位置。依次尝试(前两步参考 Ditto):
    /// <list type="number">
    /// <item>辅助功能接口 MSAA(Chrome/Edge/Electron/Office 等多数程序支持);</item>
    /// <item><c>GetGUIThreadInfo</c>(用系统插入符的传统控件:记事本、各种 Win32 输入框);</item>
    /// <item>UI Automation 文本模式(自绘文字的终端、WinUI/UWP 程序)。</item>
    /// </list>
    /// 都拿不到(或位置不在该窗口内,说明是过期的光标)返回 null,调用方应退回其他位置。
    /// <b>可能较慢,不要在 UI 线程直接调用</b>,用 <see cref="CaretRectAsync"/>。
    /// </summary>
    public static ScreenRect? CaretRect(nint foregroundWindow)
    {
        if (foregroundWindow == 0 || !GetWindowRect(foregroundWindow, out var bounds))
        {
            return null;
        }

        bool Inside(ScreenRect r) =>
            r.Left >= bounds.Left && r.Left <= bounds.Right && r.Top >= bounds.Top && r.Top <= bounds.Bottom;

        if (MsaaCaret.Locate(foregroundWindow) is { } msaa && Inside(msaa))
        {
            return msaa;
        }

        if (ThreadCaret(foregroundWindow) is { } caret && Inside(caret))
        {
            return caret;
        }

        return UiaCaret.Locate() is { } uia && Inside(uia) ? uia : null;
    }

    private static ScreenRect? ThreadCaret(nint foregroundWindow)
    {
        var threadId = GetWindowThreadProcessId(foregroundWindow, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.CaretWindow == 0)
        {
            return null;
        }

        var caret = info.CaretRect;
        if (caret.Right - caret.Left == 0 && caret.Bottom - caret.Top == 0 && caret.Left == 0 && caret.Top == 0)
        {
            return null; // 窗口声称有插入符但没给位置
        }

        var topLeft = new NativePoint { X = caret.Left, Y = caret.Top };
        var bottomRight = new NativePoint { X = caret.Right, Y = caret.Bottom };
        if (!ClientToScreen(info.CaretWindow, ref topLeft) || !ClientToScreen(info.CaretWindow, ref bottomRight))
        {
            return null;
        }

        return new ScreenRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    /// <summary>包含(或最接近)该点的显示器的工作区和缩放。</summary>
    public static MonitorInfo MonitorAt(ScreenPoint point)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            return new MonitorInfo(new ScreenRect(0, 0, 1920, 1080), 1.0);
        }

        var scale = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return new MonitorInfo(
            new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), scale);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref NativeMonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRect CaretRect;
    }
}
