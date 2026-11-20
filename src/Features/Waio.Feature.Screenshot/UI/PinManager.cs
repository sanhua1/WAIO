using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;

namespace Waio.Feature.Screenshot.UI;

/// <summary>管理所有贴图窗口(MAIO PinManager)。只在 UI 线程上使用。退出 WAIO 时不恢复贴图(spec 4.4)。</summary>
internal sealed class PinManager(Func<PinControls> controls) : IDisposable
{
    private readonly List<PinWindow> _pins = [];

    public IReadOnlyList<PinWindow> Pins => _pins;

    public event Action<PinWindow, PinAction>? ActionRequested;

    /// <summary>新贴出一张图(控制器据此在后台识别文字)。</summary>
    public event Action<PinWindow>? Pinned;

    /// <summary>贴一张图。<paramref name="contentOrigin"/> 是贴图内容左上角的全局坐标(物理像素)。</summary>
    public PinWindow Pin(PixelBuffer image, double scale, PxPoint contentOrigin, double zoom, string? recordId, bool showsShadow)
    {
        var pin = new PinWindow(image, scale, contentOrigin, zoom, recordId, showsShadow, controls);
        pin.ActionRequested += (p, action) => ActionRequested?.Invoke(p, action);
        _pins.Add(pin);
        pin.Show();
        Pinned?.Invoke(pin);
        return pin;
    }

    public void Close(PinWindow pin)
    {
        if (_pins.Remove(pin))
        {
            pin.Dispose();
        }
    }

    public void CloseAll()
    {
        foreach (var pin in _pins)
        {
            pin.Dispose();
        }

        _pins.Clear();
    }

    public void Dispose() => CloseAll();
}
