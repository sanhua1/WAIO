using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Waio.UI.Theming;

/// <summary>自绘玻璃要用的 Win32 / WinRT 互操作(ADR-0027)。</summary>
internal static unsafe partial class GlassNative
{
    /// <summary>窗口不出现在任何截屏 / 录屏 / 屏幕共享里(Windows 10 2004+)。玻璃取背景时只短暂设置(<see cref="GlassExclusion"/>)。</summary>
    public const uint WdaExcludeFromCapture = 0x11;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    /// <summary>等 DWM 合成完下一帧(让刚改的显示亲和性在截屏里生效)。</summary>
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    public const uint MonitorDefaultToNearest = 2;

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>窗口客户区在屏幕上的位置(物理像素)。</summary>
    public static Rect ClientOnScreen(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var client))
        {
            return default;
        }

        var origin = default(Point);
        _ = ClientToScreen(hwnd, ref origin);
        return new Rect { Left = origin.X, Top = origin.Y, Right = origin.X + client.Width, Bottom = origin.Y + client.Height };
    }

    /// <summary>显示器的整块区域(物理像素)。</summary>
    public static Rect MonitorBounds(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info.Monitor : default;
    }

    /// <summary>为整块显示器创建截屏项(<c>IGraphicsCaptureItemInterop::CreateForMonitor</c>)。</summary>
    /// <remarks>按 vtable 调:IUnknown 占 0–2,CreateForWindow 是 3,CreateForMonitor 是 4(Windows.Graphics.Capture.Interop.h)。</remarks>
    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopIid = GraphicsCaptureItemInteropIid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interopIid, out var interop));
        try
        {
            var vtable = *(nint**)interop;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[4];
            var iid = GraphicsCaptureItemIid;
            nint item;
            Marshal.ThrowExceptionForHR(create(interop, monitor, &iid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }
    /// <summary>
    /// 不要截屏时的黄色边框(<c>IGraphicsCaptureSession3::put_IsBorderRequired(false)</c>,Windows 11 起)。
    /// 项目编译用的 19041 投影里没有这个属性,所以按 vtable 直接调:IInspectable 占前 6 个槽,get 在 6、put 在 7(已对照 SDK 10.0.26100 头文件)。
    /// </summary>
    /// <returns>HRESULT;系统不支持时是 E_NOINTERFACE。</returns>
    public static int TryDisableBorder(GraphicsCaptureSession session)
    {
        var unknown = ((IWinRTObject)session).NativeObject.ThisPtr;
        var iid = GraphicsCaptureSession3Iid;
        var hr = Marshal.QueryInterface(unknown, in iid, out var session3);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            var vtable = *(nint**)session3;
            var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)vtable[7];
            return put(session3, 0);
        }
        finally
        {
            Marshal.Release(session3);
        }
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureSession3Iid = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
}
