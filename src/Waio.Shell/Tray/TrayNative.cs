using System.Runtime.InteropServices;

namespace Waio.Shell.Tray;

/// <summary>托盘图标用到的 Win32 声明(手写,不引入第三方托盘库,见 ARCHITECTURE §11)。</summary>
internal static class TrayNative
{
    public const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    public const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;

    /// <summary>气泡通知:图标用程序自己的(NIIF_USER),点击气泡时回调消息里是 NIN_BALLOONUSERCLICK。</summary>
    public const uint NiifUser = 4, NinBalloonUserClick = 0x0405;
    public const uint WmApp = 0x8000;
    public const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B, WmNull = 0;
    public const uint MfString = 0, MfSeparator = 0x800;
    public const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;
    public const nint HwndMessage = -3;
    public const nint IdiApplication = 32512;

    public delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    public static extern nint LoadIconW(nint instance, nint iconName);

    public const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    public const int SmCxSmIcon = 49, SmCySmIcon = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpmParams);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint Size;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }
}
