using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>一个显示器。<paramref name="Bounds"/> 是全局物理像素;<paramref name="Scale"/> 是缩放比例(150% = 1.5)。</summary>
internal sealed record DisplayInfo(nint Monitor, PxRect Bounds, double Scale);

/// <summary>冻结的显示器画面(MAIO FrozenDisplay)。</summary>
internal sealed record FrozenDisplay(DisplayInfo Display, PixelBuffer Image);

internal sealed class CaptureException(string message) : Exception(message);

/// <summary>
/// 截屏(ADR-0015)。MAIO 用 ScreenCaptureKit;Windows 上用 GDI <c>BitBlt</c> 从屏幕 DC 复制:
/// 同步、单屏 4K 约 20~40 ms、不需要 D3D 设备和异步帧池,带 <c>CAPTUREBLT</c> 时菜单、悬浮提示等分层窗口也能截进去,
/// 而且截到的就是 DWM 合成后的画面。冻结单帧时 BitBlt 已经够快,WGC 留给以后的长截图 / 录屏(需要连续帧)。
/// 不含鼠标指针(BitBlt 不画指针),对应 MAIO 的 <c>showsCursor = false</c>。
/// </summary>
internal static class ScreenCapturer
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint PwRenderFullContent = 2;

    /// <summary>当前所有显示器(主屏在前)。</summary>
    public static IReadOnlyList<DisplayInfo> Displays()
    {
        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var r = info.Monitor;
                var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
                var display = new DisplayInfo(monitor, PxRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom), scale);
                if ((info.Flags & 1) != 0)
                {
                    result.Insert(0, display); // MONITORINFOF_PRIMARY
                }
                else
                {
                    result.Add(display);
                }
            }

            return true;
        }, 0);
        return result;
    }

    /// <summary>截取每个显示器(MAIO captureAll)。</summary>
    public static IReadOnlyList<FrozenDisplay> Capture(IReadOnlyList<DisplayInfo> displays) =>
        [.. displays.Select(d => new FrozenDisplay(d, Capture(d.Bounds)))];

    /// <summary>截取屏幕上的一块(全局物理像素)。</summary>
    /// <param name="layered">
    /// 带 <c>CAPTUREBLT</c>(把菜单、悬浮提示等分层窗口也截进去)。长截图每秒截几十次,不带它:
    /// 有报告说它会让鼠标指针闪烁;DWM 合成下普通 <c>SRCCOPY</c> 截到的也是合成后的画面。
    /// </param>
    public static PixelBuffer Capture(PxRect rect, bool layered = true)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            throw new CaptureException("拿不到屏幕(桌面被锁定时会这样)");
        }

        try
        {
            return WithBitmap(screen, rect.Width, rect.Height, memory =>
                BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, layered ? SrcCopy | CaptureBlt : SrcCopy));
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// 截取单个窗口(MAIO <c>captureWindow</c>:被挡住的部分也能截到,不带阴影)。
    /// 用 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 让窗口把自己画出来(Chrome、WinUI 等硬件加速窗口也行),再裁到 DWM 的可见边界;
    /// 失败时退回从屏幕复制这个区域。
    /// </summary>
    public static PixelBuffer CaptureWindow(nint window, PxRect frame)
    {
        if (GetWindowRect(window, out var outer) && outer.Right > outer.Left && outer.Bottom > outer.Top)
        {
            var screen = GetDC(0);
            try
            {
                var whole = WithBitmap(screen, outer.Right - outer.Left, outer.Bottom - outer.Top, memory =>
                    PrintWindow(window, memory, PwRenderFullContent));
                return whole.Crop(frame.Offset(-outer.Left, -outer.Top));
            }
            catch (CaptureException)
            {
                // 退回下面的屏幕复制
            }
            finally
            {
                _ = ReleaseDC(0, screen);
            }
        }

        return Capture(frame);
    }

    /// <summary>建一张 32 位 DIB,让 <paramref name="draw"/> 往里画,再拷出像素。</summary>
    private static PixelBuffer WithBitmap(nint reference, int width, int height, Func<nint, bool> draw)
    {
        if (width <= 0 || height <= 0)
        {
            throw new CaptureException($"截图区域是空的({width}×{height})");
        }

        var memory = CreateCompatibleDC(reference);
        var header = new BitmapInfoHeader
        {
            Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        var bitmap = CreateDIBSection(reference, ref header, 0, out var bits, 0, 0);
        if (memory == 0 || bitmap == 0)
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            throw new CaptureException($"内存不足,无法创建 {width}×{height} 的图像");
        }

        var old = SelectObject(memory, bitmap);
        try
        {
            if (!draw(memory))
            {
                throw new CaptureException($"系统截图失败(错误码 {Marshal.GetLastWin32Error()})");
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            PixelBuffer.MakeOpaque(pixels);
            return new PixelBuffer(width, height, pixels);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
        }
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
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
        // BITMAPINFO = 头(40 字节,Size 填 40)+ bmiColors[1];BI_RGB 用不到颜色表,留一个占位
        private readonly uint _colors;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);
}
