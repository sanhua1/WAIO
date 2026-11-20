using System.Runtime.InteropServices;

namespace Waio.Feature.Screenshot.UI;

/// <summary>贴图窗口用到的 Win32 声明(分层窗口、菜单、鼠标跟踪)。</summary>
internal static class PinNative
{
    public const uint WsPopup = 0x80000000;
    public const uint WsExTopmost = 0x8, WsExToolWindow = 0x80, WsExLayered = 0x80000;

    public const uint WmDestroy = 0x0002, WmActivate = 0x0006, WmSetCursor = 0x0020, WmContextMenu = 0x007B;
    public const uint WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcLButtonDown = 0x00A1, WmNcLButtonDblClk = 0x00A3;
    public const uint WmNcRButtonDown = 0x00A4, WmNcRButtonUp = 0x00A5, WmNcMouseLeave = 0x02A2;
    public const uint WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmSysChar = 0x0106, WmTimer = 0x0113;
    public const uint WmMouseWheel = 0x020A, WmExitSizeMove = 0x0232, WmNull = 0;
    public const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203;
    public const uint WmCaptureChanged = 0x0215, WmMouseLeave = 0x02A3;
    public const uint CsDblClks = 0x8;

    public const int HtTransparent = -1, HtClient = 1, HtCaption = 2;
    public const uint TmeLeave = 0x2, TmeNonClient = 0x10;
    public const uint UlwAlpha = 0x2;
    public const byte AcSrcOver = 0, AcSrcAlpha = 1;
    public const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpShowWindow = 0x40, SwpHideWindow = 0x80;
    public const int SwHide = 0, SwShowNoActivate = 4;
    public const nint HwndTopmost = -1;

    public const uint MfString = 0, MfGrayed = 0x1, MfChecked = 0x8, MfPopup = 0x10, MfSeparator = 0x800;
    public const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;

    public const int VkBack = 0x08, VkEscape = 0x1B, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    public const int VkAdd = 0x6B, VkSubtract = 0x6D, VkOemPlus = 0xBB, VkOemMinus = 0xBD;

    public delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TrackMouseEventInfo
    {
        public uint Size;
        public uint Flags;
        public nint Hwnd;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    public static extern nint LoadCursorW(nint instance, nint name);

    [DllImport("user32.dll")]
    public static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    public static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(
        nint hwnd, nint screenDc, ref Point position, ref Size size, nint sourceDc, ref Point sourcePosition, uint key,
        ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(nint hwnd, nuint id);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    public static bool IsDown(int key) => GetKeyState(key) < 0;

    public static short HighWord(nuint value) => unchecked((short)((ulong)value >> 16));

    public static (int X, int Y) PointFromLParam(nint lParam) => (unchecked((short)(lParam & 0xFFFF)), unchecked((short)((lParam >> 16) & 0xFFFF)));
}
