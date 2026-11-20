using System.Runtime.InteropServices;

namespace Waio.Core.Input;

/// <summary>
/// 输入相关的 Win32 声明。**全仓库只有这个目录允许出现 SetWindowsHookEx / SendInput 等**(架构测试强制)。
/// 手写 P/Invoke 而不引入 CsWin32:M0 只需要十来个函数,少一个依赖(ARCHITECTURE §11)。
/// </summary>
internal static class InputNative
{
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_APP_REINSTALL = 0x8001;
    public const uint WM_USER = 0x0400;
    public const uint PM_NOREMOVE = 0;
    public const int LLKHF_INJECTED = 0x10;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>WAIO 合成事件的 dwExtraInfo 魔数("WAO1")。钩子据此跳过自己发出的事件,避免死循环。</summary>
    public static readonly nint Magic = 0x57414F31;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint HookProc(int nCode, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>x64 下 INPUT 为 40 字节:4 字节类型 + 4 字节填充 + 32 字节联合体(以 MOUSEINPUT 为最大成员)。</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(8)]
        public MOUSEINPUT Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>低位 = 锁定键(CapsLock 等)的锁定状态(当前线程视角)。</summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    /// <summary>虚拟键码 → 扫描码(当前键盘布局)。</summary>
    public const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? moduleName);
}
