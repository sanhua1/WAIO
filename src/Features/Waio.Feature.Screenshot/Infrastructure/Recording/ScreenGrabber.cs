using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>
/// 录屏的取帧(ADR-0021):固定一块选区反复 <c>BitBlt(SRCCOPY)</c>,复用同一张 DIB(每秒几十帧,不能每帧新分配 8 MB),
/// 需要时把鼠标指针画进去(BitBlt 截不到指针)。不带 <c>CAPTUREBLT</c>:据报会让指针闪烁。
/// 不是线程安全的,只在录制线程上用。
/// </summary>
internal sealed unsafe class ScreenGrabber : IFrameGrabber
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CursorShowing = 0x1;
    private const uint DiNormal = 0x3;

    private readonly PxRect _rect;
    private readonly bool _cursor;
    private nint _screen;
    private nint _memory;
    private nint _bitmap;
    private nint _old;
    private readonly byte* _bits;

    public ScreenGrabber(PxRect rect, bool cursor)
    {
        _rect = rect;
        _cursor = cursor;
        _screen = GetDC(0);
        _memory = CreateCompatibleDC(_screen);
        var header = new ScreenCapturer.BitmapInfoHeader { Size = 40, Width = rect.Width, Height = -rect.Height, Planes = 1, BitCount = 32 };
        _bitmap = CreateDIBSection(_screen, ref header, 0, out var bits, 0, 0);
        if (_screen == 0 || _memory == 0 || _bitmap == 0)
        {
            Dispose();
            throw new RecordingException($"无法创建 {rect.Width}×{rect.Height} 的截图缓冲区");
        }

        _bits = (byte*)bits;
        _old = SelectObject(_memory, _bitmap);
    }

    public int Width => _rect.Width;

    public int Height => _rect.Height;

    /// <summary>截一帧。返回的内存属于抓取器,下一次 <see cref="Grab"/> 前有效;Alpha 已设为 255。</summary>
    public Span<byte> Grab()
    {
        if (!BitBlt(_memory, 0, 0, _rect.Width, _rect.Height, _screen, _rect.X, _rect.Y, SrcCopy))
        {
            throw new CaptureException($"系统截图失败(错误码 {Marshal.GetLastWin32Error()})");
        }

        if (_cursor)
        {
            DrawCursor();
        }

        _ = GdiFlush();
        var pixels = new Span<byte>(_bits, _rect.Width * _rect.Height * 4);
        PixelBuffer.MakeOpaque(pixels);
        return pixels;
    }

    private void DrawCursor()
    {
        var info = new CursorInfo { Size = (uint)sizeof(CursorInfo) };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == 0)
        {
            return;
        }

        if (!GetIconInfo(info.Cursor, out var icon))
        {
            return;
        }

        try
        {
            var x = info.X - (int)icon.HotspotX - _rect.X;
            var y = info.Y - (int)icon.HotspotY - _rect.Y;
            _ = DrawIconEx(_memory, x, y, info.Cursor, 0, 0, 0, 0, DiNormal);
        }
        finally
        {
            // GetIconInfo 会复制两张位图,必须删掉,否则每帧泄漏一个 GDI 对象
            _ = ScreenCapturer.DeleteObject(icon.Mask);
            _ = ScreenCapturer.DeleteObject(icon.Color);
        }
    }

    public void Dispose()
    {
        if (_memory != 0)
        {
            if (_old != 0)
            {
                _ = SelectObject(_memory, _old);
                _old = 0;
            }

            _ = DeleteDC(_memory);
            _memory = 0;
        }

        if (_bitmap != 0)
        {
            _ = ScreenCapturer.DeleteObject(_bitmap);
            _bitmap = 0;
        }

        if (_screen != 0)
        {
            _ = ReleaseDC(0, _screen);
            _screen = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public uint Size;
        public uint Flags;
        public nint Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref ScreenCapturer.BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
}
