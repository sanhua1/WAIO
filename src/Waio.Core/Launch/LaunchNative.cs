using System.Runtime.InteropServices;
using System.Text;

namespace Waio.Core.Launch;

/// <summary>降权启动用到的 Win32 声明。不引入 CsWin32(ARCHITECTURE §11),手写。</summary>
internal static class LaunchNative
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenDuplicate = 0x0002;
    public const uint TokenAllAccess = 0xF01FF;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const uint CreateUnicodeEnvironment = 0x00000400;

    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateTokenEx(
        nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

    public const int TokenElevationClass = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(nint token, int informationClass, out int information, int length, out int returnLength);

    /// <remarks>lpCommandLine 用 StringBuilder:系统可能修改这块缓冲区,不能传常量字符串。</remarks>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessWithTokenW(
        nint token, uint logonFlags, string? applicationName, StringBuilder commandLine, uint creationFlags,
        nint environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
