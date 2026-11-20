using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain;
using static Waio.Feature.Screenshot.UI.PinNative;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 长截图、录屏时选区外一圈细边框(MAIO FloatingPanels.border):四条纯色的 Win32 小窗口,置顶、不激活、鼠标穿透、不进任务栏,
/// 并且排除在截屏之外(<c>WDA_EXCLUDEFROMCAPTURE</c>)。边框画在选区<b>外面</b>,即使排除不生效也不会被截进去。只在 UI 线程上使用。
/// </summary>
internal sealed class ScrollCaptureBorder : IDisposable
{
    private const string ClassPrefix = "WAIO.CaptureBorder.";
    private const uint WsExNoActivate = 0x08000000, WsExTransparent = 0x20;
    private const uint LwaAlpha = 0x2;
    private const uint WdaExcludeFromCapture = 0x11;

    /// <summary>Windows 默认强调色 #0078D4(COLORREF 是 0x00BBGGRR)。</summary>
    public const uint AccentColor = 0x00D47800;

    /// <summary>录制中的红色 #E81123。</summary>
    public const uint RecordingColor = 0x002311E8;

    private static readonly WndProc Procedure = (hwnd, message, wParam, lParam) => DefWindowProcW(hwnd, message, wParam, lParam);
    private static readonly HashSet<uint> s_registered = [];

    private readonly List<nint> _windows = [];

    /// <param name="color">COLORREF(0x00BBGGRR)。</param>
    public ScrollCaptureBorder(PxRect selection, double scale, uint color = AccentColor)
        : this(ScrollCaptureLayout.Border(selection, Thickness(scale)), color)
    {
    }

    /// <summary>直接给出四条边的位置(全屏录屏画在屏幕里面,见 <see cref="ScrollCaptureLayout.InnerBorder"/>)。</summary>
    public ScrollCaptureBorder(IReadOnlyList<PxRect> bars, uint color)
    {
        var className = Register(color);
        var instance = GetModuleHandleW(null);
        foreach (var bar in bars)
        {
            var hwnd = CreateWindowExW(
                WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate, className, null, WsPopup,
                bar.X, bar.Y, bar.Width, bar.Height, 0, 0, instance, 0);
            if (hwnd == 0)
            {
                continue;
            }

            _ = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha); // 分层 + 穿透:点在边框上也会落到下面的窗口
            _ = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
            _windows.Add(hwnd);
        }
    }

    /// <summary>边框粗细:2 个逻辑像素。</summary>
    public static int Thickness(double scale) => Math.Max(1, (int)Math.Round(2 * scale));

    /// <summary>系统强调色(COLORREF);取不到时用默认蓝。和截图选区的边框同一个颜色(OverlayWindow.AccentColor)。</summary>
    public static uint SystemAccent()
    {
        try
        {
            var c = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            return (uint)(c.B << 16 | c.G << 8 | c.R);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return AccentColor;
        }
    }

    public void Show()
    {
        foreach (var hwnd in _windows)
        {
            _ = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
    }

    public void Dispose()
    {
        foreach (var hwnd in _windows)
        {
            _ = DestroyWindow(hwnd);
        }

        _windows.Clear();
    }

    /// <summary>每种颜色一个窗口类(背景画刷属于窗口类,整个窗口就是这个颜色,不用自己画)。</summary>
    private static string Register(uint color)
    {
        var className = ClassPrefix + color.ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
        if (s_registered.Contains(color))
        {
            return className;
        }

        var wndClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandleW(null),
            Background = CreateSolidBrush(color),
            ClassName = className,
        };
        if (RegisterClassExW(ref wndClass) == 0 && Marshal.GetLastWin32Error() != 1410 /* 类已存在 */)
        {
            throw new InvalidOperationException($"注册长截图边框窗口类失败:{Marshal.GetLastWin32Error()}");
        }

        s_registered.Add(color);
        return className;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
