using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Waio.Feature.Screenshot.Domain;
using Waio.UI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>
/// 全屏录屏的取帧(ADR-0030):Windows.Graphics.Capture 截整块显示器,画面在显卡上合成,只在有新帧时读回内存。
/// 2026-11-12 本机实测 BitBlt 截一帧 4K 要约 68 ms(上限约 14 帧/秒,真实录制只有 6 帧/秒),所以全屏改用它。
/// 排除在截屏之外的窗口(边框、HUD)同样不会进画面;鼠标指针由系统画。系统没有新画面时返回上一帧。
/// </summary>
internal sealed class GpuScreenGrabber : IFrameGrabber
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(1);

    private readonly CanvasDevice _device = new();
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly ManualResetEventSlim _firstFrame = new();
    private readonly object _gate = new();
    private readonly PxRect _crop; // 相对显示器左上角
    private readonly byte[] _pixels;
    private Direct3D11CaptureFrame? _latest;

    /// <param name="monitor">显示器句柄。</param>
    /// <param name="monitorBounds">显示器的全局物理像素范围。</param>
    /// <param name="rect">要截的区域(在显示器内,全局物理像素)。</param>
    public GpuScreenGrabber(nint monitor, PxRect monitorBounds, PxRect rect, bool cursor)
    {
        _crop = new PxRect(rect.X - monitorBounds.X, rect.Y - monitorBounds.Y, rect.Width, rect.Height);
        _pixels = new byte[rect.Width * rect.Height * 4];
        try
        {
            var item = ScreenCaptureInterop.ForMonitor(monitor);
            if (item.Size.Width < _crop.Right || item.Size.Height < _crop.Bottom)
            {
                throw new RecordingException($"显示器大小 {item.Size.Width}×{item.Size.Height} 放不下 {rect.Width}×{rect.Height}");
            }

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = cursor;
            _ = ScreenCaptureInterop.TryDisableBorder(_session); // 不要系统的黄框(我们自己画了强调色边框)
            _session.StartCapture();
        }
        catch (Exception ex) when (ex is not RecordingException)
        {
            Dispose();
            throw new RecordingException($"GPU 截屏不可用:{ex.Message}");
        }

        if (!_firstFrame.Wait(FirstFrameTimeout))
        {
            Dispose();
            throw new RecordingException("GPU 截屏一秒内没有出画面");
        }
    }

    public int Width => _crop.Width;

    public int Height => _crop.Height;

    public Span<byte> Grab()
    {
        Direct3D11CaptureFrame? frame;
        lock (_gate)
        {
            frame = _latest;
            _latest = null;
        }

        if (frame is not null)
        {
            using (frame)
            using (var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface))
            {
                bitmap.GetPixelBytes(_pixels.AsBuffer(), _crop.X, _crop.Y, _crop.Width, _crop.Height);
            }
        }

        return _pixels;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _pool?.Dispose();
        lock (_gate)
        {
            _latest?.Dispose();
            _latest = null;
        }

        _device.Dispose();
    }

    /// <summary>系统线程:只留最新的一帧(录制线程来不及取时,旧帧直接丢掉)。</summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        lock (_gate)
        {
            _latest?.Dispose();
            _latest = frame;
        }

        _firstFrame.Set();
    }
}
