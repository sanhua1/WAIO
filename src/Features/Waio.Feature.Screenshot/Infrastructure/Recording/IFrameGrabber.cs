namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>录屏取帧:BitBlt(<see cref="ScreenGrabber"/>,区域录屏)或 GPU(<see cref="GpuScreenGrabber"/>,全屏录屏,ADR-0030)。只在录制线程上用。</summary>
internal interface IFrameGrabber : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>当前画面(BGRA,从上到下,宽 × 高 × 4)。返回的内存属于抓取器,下一次调用前有效。</summary>
    Span<byte> Grab();
}
