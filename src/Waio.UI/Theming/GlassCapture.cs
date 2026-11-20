using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Waio.UI.Theming;

/// <summary>
/// 一块显示器的"窗口背后"画面,供这块屏上所有玻璃窗口共用(ADR-0027)。
/// <para>
/// 不持续截屏,而是<b>按需取一帧</b>(<see cref="Request"/>):短暂把 WAIO 窗口排除在截屏外(<see cref="GlassExclusion"/>)→
/// 开一个截屏会话拿第一帧 → 立即关会话、恢复。平时 WAIO 窗口能被任何截图工具截到。
/// </para>
/// 每帧只保留一张缩小到 1/<see cref="Downscale"/> 的副本:模糊本来就要丢细节,在小图上模糊又快又省显存。
/// 截屏回调在线程池,处理一律排到 UI 线程。
/// </summary>
internal sealed class GlassCapture : IDisposable
{
    public const int Downscale = 4;

    /// <summary>等第一帧最多这么久;超时就放弃这次(恢复显示亲和性,下次再取)。</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(600);

    private readonly nint _monitor;
    private readonly CanvasDevice _device;
    private readonly DispatcherQueue _ui;
    private readonly Func<IEnumerable<nint>> _windows;
    private readonly Action<GlassCapture> _frameReady;

    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private DispatcherQueueTimer? _timeout;
    private bool _again;
    private int _pulse;

    /// <param name="windows">取背景时要排除的窗口(所有玻璃窗口)。</param>
    public GlassCapture(nint monitor, CanvasDevice device, DispatcherQueue ui, Func<IEnumerable<nint>> windows, Action<GlassCapture> frameReady)
    {
        _monitor = monitor;
        _device = device;
        _ui = ui;
        _windows = windows;
        _frameReady = frameReady;
    }

    public nint Monitor => _monitor;

    /// <summary>最近一帧的缩小副本;还没取到过时为 null。</summary>
    public CanvasRenderTarget? Latest { get; private set; }

    /// <summary>显示器在屏幕上的区域(物理像素),取这一帧时的值。</summary>
    public GlassNative.Rect Bounds { get; private set; }

    /// <summary>最近一次取到画面的时间。</summary>
    public DateTime CapturedAt { get; private set; }

    /// <summary>关掉黄色边框的结果(HRESULT,0 = 成功),用于日志;还没取过时为 null。</summary>
    public int? BorderResult { get; private set; }

    public bool IsBusy => _session is not null;

    /// <summary>取一帧新的背景(UI 线程)。正在取时记一笔,取完再取一次;WAIO 自己截图 / 录屏期间不取。</summary>
    public void Request()
    {
        if (_session is not null)
        {
            _again = true;
            return;
        }

        if (!GlassExclusion.Begin(_windows()))
        {
            return; // 暂停中
        }

        try
        {
            _ = GlassNative.DwmFlush(); // 让"排除"在截屏合成里生效后再开会话,第一帧里就没有 WAIO 窗口
            Bounds = GlassNative.MonitorBounds(_monitor);
            var item = GlassNative.CreateItemForMonitor(_monitor);
            var pulse = ++_pulse;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            _pool.FrameArrived += (sender, _) =>
            {
                var frame = sender.TryGetNextFrame();
                if (frame is not null && !_ui.TryEnqueue(() => Finish(pulse, frame)))
                {
                    frame.Dispose();
                }
            };
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            var border = GlassNative.TryDisableBorder(_session);
            BorderResult ??= border;
            _session.StartCapture();

            _timeout ??= CreateTimeout();
            _timeout.Start();
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Dispose()
    {
        Close();
        Latest?.Dispose();
        Latest = null;
    }

    /// <summary>UI 线程:第一帧到了(或超时,frame 为 null)。</summary>
    private void Finish(int pulse, Direct3D11CaptureFrame? frame)
    {
        if (pulse != _pulse || _session is null)
        {
            frame?.Dispose(); // 过期的帧(超时之后才到)
            return;
        }

        Close();
        if (frame is not null)
        {
            using (frame)
            {
                Store(frame);
            }

            _frameReady(this);
        }

        if (_again)
        {
            _again = false;
            Request();
        }
    }

    /// <summary>关会话并恢复 WAIO 窗口的显示亲和性。</summary>
    private void Close()
    {
        _timeout?.Stop();
        GlassExclusion.End();
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
    }

    private void Store(Direct3D11CaptureFrame frame)
    {
        var size = frame.ContentSize;
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
        var width = Math.Max(1, size.Width / Downscale);
        var height = Math.Max(1, size.Height / Downscale);
        if (Latest is null || (int)Latest.SizeInPixels.Width != width || (int)Latest.SizeInPixels.Height != height)
        {
            Latest?.Dispose();
            Latest = new CanvasRenderTarget(_device, width, height, 96);
        }

        using (var session = Latest.CreateDrawingSession())
        {
            session.DrawImage(bitmap, new Windows.Foundation.Rect(0, 0, width, height),
                new Windows.Foundation.Rect(0, 0, size.Width, size.Height), 1, CanvasImageInterpolation.Linear);
        }

        CapturedAt = DateTime.UtcNow;
    }

    private DispatcherQueueTimer CreateTimeout()
    {
        var timer = _ui.CreateTimer();
        timer.Interval = FrameTimeout;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Finish(_pulse, null);
        return timer;
    }
}
