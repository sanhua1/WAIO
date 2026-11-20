using Windows.Graphics.Capture;

namespace Waio.UI;

/// <summary>
/// Windows.Graphics.Capture 的两个互操作调用,给功能用(全屏录屏的 GPU 取帧,ADR-0030)。
/// 实现在 <see cref="Theming.GlassNative"/>(自绘玻璃取背景也用它们,ADR-0027)。
/// </summary>
public static class ScreenCaptureInterop
{
    /// <summary>整块显示器的截屏项。</summary>
    public static GraphicsCaptureItem ForMonitor(nint monitor) => Theming.GlassNative.CreateItemForMonitor(monitor);

    /// <summary>不要截屏时的黄色边框(Windows 11 起);返回 HRESULT,不支持时是负数。</summary>
    public static int TryDisableBorder(GraphicsCaptureSession session) => Theming.GlassNative.TryDisableBorder(session);
}
