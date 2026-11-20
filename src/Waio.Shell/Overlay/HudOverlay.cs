using Microsoft.UI.Dispatching;
using Waio.Kit.Services;

namespace Waio.Shell.Overlay;

/// <summary>
/// <see cref="IOverlayService"/> 的 WinUI 实现。可从任意线程调用,实际渲染切到 UI 线程;
/// 调用立即返回(不等 HUD 显示完),连续调用复用同一个窗口并重置计时。
/// </summary>
internal sealed class HudOverlay(DispatcherQueue dispatcher) : IOverlayService
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(1800);

    private HudWindow? _window;
    private DispatcherQueueTimer? _hideTimer;

    public ValueTask ShowHudAsync(string text, TimeSpan? duration = null)
    {
        dispatcher.TryEnqueue(() => Show(text, duration ?? DefaultDuration));
        return ValueTask.CompletedTask;
    }

    public void Close()
    {
        dispatcher.TryEnqueue(() =>
        {
            _hideTimer?.Stop();
            _window?.Close();
            _window = null;
        });
    }

    private void Show(string text, TimeSpan duration)
    {
        _window ??= new HudWindow();
        _window.Present(text);

        _hideTimer ??= CreateTimer();
        _hideTimer.Interval = duration;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _window?.AppWindow.Hide();
        return timer;
    }
}
