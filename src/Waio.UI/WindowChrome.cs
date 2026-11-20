using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Waio.UI;

/// <summary>窗口外观与行为工具:浮层窗口(置顶、不抢焦点、不进任务栏)等。</summary>
public static class WindowChrome
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;

    /// <summary>
    /// 把窗口变成"浮层":无边框、置顶、不进任务栏、<b>显示时不抢焦点</b>。
    /// HUD、剪贴板/启动台这类要"粘贴回原窗口"的面板必须用它(ARCHITECTURE §6)。
    /// </summary>
    /// <param name="activatable">
    /// true = 可以获得焦点(剪贴板面板要在搜索框里打字);false = 显示时不抢焦点(HUD)。
    /// </param>
    public static void MakeOverlay(Window window, bool activatable = false)
    {
        var appWindow = window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        appWindow.IsShownInSwitchers = false;

        var handle = WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var added = activatable ? WsExToolWindow : WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(handle, GwlExStyle, new nint(style | added));
    }

    /// <summary>
    /// 把窗口激活并带到前台。Windows 不允许后台进程随便抢前台(前台锁),所以用 AttachThreadInput
    /// 暂时挂到当前前台线程的输入队列上,这是让弹出式面板可靠获得焦点的标准做法。
    /// </summary>
    /// <returns>窗口最终是否成为了前台窗口。</returns>
    public static bool ActivateForeground(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        ForceForeground(hwnd);
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>窗口当前是不是前台窗口。</summary>
    public static bool IsForeground(Window window) => GetForegroundWindow() == WindowNative.GetWindowHandle(window);

    /// <summary>
    /// 把窗口变成干净的弹出面板(像 Windows 11 的开始菜单、剪贴板历史):
    /// 窗口样式改成纯 <c>WS_POPUP</c>(去掉标题栏/可拉伸边框留下的那一圈非客户区),
    /// Windows 11 圆角,系统描边改成很淡的一像素。Windows 10 上圆角和描边设置无效果、也不报错。
    /// 必须在 <see cref="MakeOverlay"/> 之后调用(后者会改动窗口样式)。
    /// </summary>
    /// <param name="dark">深色主题:决定描边颜色。</param>
    public static void MakePopupSurface(Window window, bool dark)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox | WsBorder | WsDlgFrame);
        style |= WsPopup;
        SetWindowLongPtr(hwnd, GwlStyle, new nint(style));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        var corner = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        SetPopupBorder(window, dark);
    }

    /// <summary>
    /// 弹出面板的一像素系统描边颜色(玻璃边缘)。外观切换深浅色时由 <see cref="Theming.Glass"/> 重新调用。
    /// </summary>
    public static void SetPopupBorder(Window window, bool dark)
    {
        // COLORREF 是 0x00BBGGRR;灰色三个分量相同
        var border = dark ? 0x00484848 : 0x00E0E0E0;
        _ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(window), DwmwaBorderColor, ref border, sizeof(int));
    }

    /// <summary>
    /// 前台窗口是不是这个窗口自己,或者属于它(它弹出的右键菜单等以它为所有者的窗口)。
    /// 用来判断"用户是不是点到别处去了"。
    /// </summary>
    public static bool OwnsForeground(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var foreground = GetForegroundWindow();
        return foreground == hwnd || (foreground != 0 && GetAncestor(foreground, GaRootOwner) == hwnd);
    }

    /// <summary>当前前台窗口句柄。</summary>
    public static nint Foreground() => GetForegroundWindow();

    /// <summary>
    /// 窗口是不是 WAIO 自己的(同一进程)。弹出面板判断"点到别处就关闭"时用:
    /// 前台换成 WAIO 的另一个窗口(启动台里按 Win+V 弹出剪贴板、Alt+Q 翻译,截图写字时调剪贴板)不算点到别处,
    /// 几个面板应当共存。
    /// </summary>
    public static bool IsOwnWindow(nint hwnd) =>
        hwnd != 0 && GetWindowThreadProcessId(hwnd, out var processId) != 0 && processId == (uint)Environment.ProcessId;

    /// <summary>
    /// 允许接下来启动的程序把它的窗口带到前台(AllowSetForegroundWindow(ASFW_ANY))。
    /// 只有前台进程调用才有效,所以启动台要在面板还是前台、关闭面板之前调用;否则新程序的窗口可能躲在后面、只闪任务栏按钮。
    /// </summary>
    public static void AllowForegroundHandoff() => _ = AllowSetForegroundWindow(AsfwAny);

    private const uint AsfwAny = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    private const int GwlStyle = -16;
    private const long WsPopup = 0x80000000;
    private const long WsCaption = 0x00C00000;
    private const long WsBorder = 0x00800000;
    private const long WsDlgFrame = 0x00400000;
    private const long WsThickFrame = 0x00040000;
    private const long WsSysMenu = 0x00080000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint GaRootOwner = 3;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    /// <summary>
    /// 把焦点还给指定窗口(粘贴前切回原程序),并<b>等它真的成为前台</b>再返回——否则紧接着发出的 Ctrl+V 会落到错误的窗口。
    /// 超时仍未成功返回 false。
    /// <para>
    /// 窗口在这期间已经隐藏的,不再还原(返回 false):<see cref="ForceForeground"/> 会 ShowWindow,
    /// 会把已经关掉的面板重新显示出来。踩坑:启动台开着时截图,遮罩抢前台 → 启动台按"点到别处"关闭并停止监视;
    /// 截图结束把前台还给启动台 → 启动台被重新显示,却再也不会自己关闭。
    /// </para>
    /// </summary>
    public static async Task<bool> RestoreForegroundAsync(nint hwnd, TimeSpan timeout)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return false;
        }

        if (GetForegroundWindow() != hwnd)
        {
            ForceForeground(hwnd);
        }

        var deadline = DateTime.UtcNow + timeout;
        while (GetForegroundWindow() != hwnd)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        await Task.Delay(40).ConfigureAwait(false); // 刚激活时窗口的焦点控件可能还没就绪
        return true;
    }

    private static void ForceForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
            && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            ShowWindow(hwnd, SwShow);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    /// <summary>不激活地显示窗口(不会把前台焦点从用户正在用的程序那里抢走)。</summary>
    public static void ShowWithoutActivation(Window window)
    {
        ShowWindow(WindowNative.GetWindowHandle(window), SwShowNoActivate);
    }

    /// <summary>
    /// 把普通窗口放到<b>鼠标所在显示器</b>的正中,尺寸按该显示器的缩放比例换算,并且不超过工作区的 <paramref name="maxFraction"/>。
    /// 直接用物理像素设尺寸的话,150%/200% 缩放下窗口会小得装不下内容(用户反馈设置窗口要拉滚动条)。
    /// </summary>
    public static void CenterOnCurrentMonitor(Window window, double widthDip, double heightDip, double maxFraction = 0.9)
    {
        var monitor = ScreenLocator.MonitorAt(ScreenLocator.CursorPosition());
        var work = monitor.WorkArea;
        var width = (int)Math.Min(widthDip * monitor.Scale, work.Width * maxFraction);
        var height = (int)Math.Min(heightDip * monitor.Scale, work.Height * maxFraction);
        var x = work.Left + (work.Width - width) / 2;
        var y = work.Top + (work.Height - height) / 2;

        // 先挪到目标显示器上:跨不同缩放的显示器时系统会按新 DPI 自动缩放窗口,挪过去之后再设最终尺寸,才不会被它覆盖
        window.AppWindow.Move(new PointInt32(x, y));
        window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>把窗口放到工作区底部居中(以 DIP 为单位的尺寸,按所在显示器缩放换算)。</summary>
    public static void PlaceBottomCenter(Window window, double widthDip, double heightDip, double marginDip = 48)
    {
        var appWindow = window.AppWindow;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var scale = GetScaleFactor(window);
        var width = (int)(widthDip * scale);
        var height = (int)(heightDip * scale);
        var work = area.WorkArea;
        appWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + work.Height - height - (int)(marginDip * scale),
            width,
            height));
    }

    private static double GetScaleFactor(Window window)
    {
        var dpi = GetDpiForWindow(WindowNative.GetWindowHandle(window));
        return dpi / 96.0;
    }

    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
}

