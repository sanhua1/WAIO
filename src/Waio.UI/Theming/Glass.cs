using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Waio.UI.Theming;

/// <summary>
/// 全局外观(ADR-0026 / ADR-0027):当前玻璃参数、实时切换、给窗口挂上自绘玻璃。只在 UI 线程使用
/// (<see cref="SuspendBackgroundRefresh"/> 除外)。
/// <list type="number">
/// <item>Shell 启动时、创建任何窗口之前调 <see cref="Initialize"/>。</item>
/// <item>每个窗口设置好 Content 之后调 <see cref="Attach"/>(不要再用 Mica / DesktopAcrylicBackdrop)。</item>
/// <item>"外观"设置页改动时调 <see cref="Apply"/>,所有窗口立即重画。</item>
/// </list>
/// 玻璃完全自己画(取窗口背后的画面 → 模糊 → 鲜艳度 → 色调),不依赖系统的"透明效果"开关;深浅色跟随系统。
/// </summary>
public static class Glass
{
    /// <summary>窗口开着时,背景每隔这么久重新取一次(背后的内容变了也能跟上)。取的那几十毫秒里 WAIO 窗口不出现在截屏里。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private static readonly List<GlassSurface> Surfaces = [];
    private static readonly Dictionary<nint, GlassCapture> Captures = [];
    private static ILogger? _logger;
    private static DispatcherQueue? _ui;
    private static DispatcherQueueTimer? _poll;
    private static CanvasDevice? _device;
    private static CompositionGraphicsDevice? _graphics;
    private static bool _loggedBorder;

    /// <summary>当前外观。</summary>
    public static GlassTheme Current { get; private set; } = GlassTheme.Default;

    /// <summary>外观变化后(已经应用到资源和窗口)触发,参数是变化前的外观。</summary>
    public static event Action<GlassTheme>? Changed;

    /// <summary>启动时调用一次,必须早于任何窗口。</summary>
    public static void Initialize(GlassTheme theme, ILogger? logger)
    {
        _logger = logger;
        _ui = DispatcherQueue.GetForCurrentThread();
        Current = theme.Clamped();
        GlassResources.Apply(Current, SystemAccent(), logger);

        // 兜底轮询(每 250 ms):功能用 Win32 ShowWindow 显示面板时不一定有 XAML 事件;顺便定期刷新背景
        _poll = _ui.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(250);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();

        logger?.LogInformation("外观:自绘玻璃,模糊 {Blur:0} px,鲜艳度 {Saturation:0.00},色调浓度 {Tint:0.00},卡片浓度 {Surface:0.00},圆角 {Corners}",
            Current.BlurRadius, Current.Saturation, Current.TintOpacity, Current.SurfaceOpacity, Current.Corners);
    }

    /// <summary>切换外观,所有打开的窗口立即重画。</summary>
    public static void Apply(GlassTheme theme)
    {
        var previous = Current;
        Current = theme.Clamped();
        if (Current == previous)
        {
            return;
        }

        GlassResources.Apply(Current, SystemAccent(), _logger);
        if (previous.Corners != Current.Corners)
        {
            GlassShape.Update(Surfaces.Select(s => s.Window.Content).OfType<UIElement>().ToList());
        }

        foreach (var surface in Surfaces.ToList())
        {
            surface.Render();
        }

        Changed?.Invoke(previous);
    }

    /// <summary>窗口实际是不是深色(跟随系统;拿不到窗口内容时看应用主题)。</summary>
    public static bool IsDark(Window? window = null) =>
        window?.Content is FrameworkElement { ActualTheme: var actual } && actual != ElementTheme.Default
            ? actual == ElementTheme.Dark
            : Application.Current.RequestedTheme == ApplicationTheme.Dark;

    /// <summary>
    /// 给窗口挂上自绘玻璃。窗口关闭时自动解除。
    /// 窗口平时能被任何截图工具截到;只在取背景的几十毫秒里排除在截屏外(<see cref="GlassExclusion"/>)。
    /// </summary>
    /// <param name="popup">纯 WS_POPUP 弹出面板(已调过 <see cref="WindowChrome.MakePopupSurface"/>):深浅色变化时一并更新系统描边颜色。</param>
    public static void Attach(Window window, bool popup = false)
    {
        var surface = new GlassSurface(window, popup);
        Surfaces.Add(surface);

        window.AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange)
            {
                Sync(surface, refresh: true);
            }
            else if (e.DidPositionChange || e.DidSizeChange)
            {
                Sync(surface, refresh: false); // 背景是"排除了 WAIO 窗口"的整屏画面,移动时直接用它重画
            }
        };
        window.Activated += (_, e) => Sync(surface, refresh: e.WindowActivationState != WindowActivationState.Deactivated);
        window.Closed += (_, _) =>
        {
            Surfaces.Remove(surface);
            surface.Capture = null;
        };
        if (window.Content is FrameworkElement root)
        {
            GlassShape.Conform(root);

            // 系统切换深浅色:色调和描边跟着变
            root.ActualThemeChanged += (_, _) =>
            {
                if (popup)
                {
                    WindowChrome.SetPopupBorder(window, IsDark(window));
                }

                surface.Render();
            };
        }

        Sync(surface, refresh: true);
    }

    /// <summary>让元素(如设置窗口的导航栏)直接持有 WAIO 自有画刷,见 <see cref="GlassResources.AttachOwned"/>。</summary>
    public static void UseOwnedResources(FrameworkElement element) => GlassResources.AttachOwned(element);
    /// <summary>
    /// 暂停取背景(可在任意线程调用):WAIO 自己截图、长截图、录屏期间用,保证截到的画面里有 WAIO 窗口。
    /// 返回的对象 Dispose 后恢复。
    /// </summary>
    public static IDisposable SuspendBackgroundRefresh() => GlassExclusion.Suspend();

    /// <summary>Win2D 绘制合成表面要用的图形设备(每个合成器一个;WAIO 只有一个 UI 线程,实际只有一个)。</summary>
    internal static CompositionGraphicsDevice? GraphicsFor(Compositor compositor)
    {
        try
        {
            _device ??= CanvasDevice.GetSharedDevice();
            if (_graphics is null || _graphics.Compositor != compositor)
            {
                _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
            }

            return _graphics;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "创建玻璃图形设备");
            return null;
        }
    }

    /// <summary>外观相关的失败只记日志(玻璃效果不对不值得让程序退出)。</summary>
    internal static void LogFailure(Exception ex, string action) =>
        _logger?.LogWarning(ex, "外观:{Action}失败", action);

    /// <summary>
    /// 一个窗口的状态变了:分配所在显示器的背景并立即重画。<paramref name="refresh"/> 为 true(刚显示、刚激活)时取一帧新背景;
    /// 移动 / 缩放时不取,直接用已有的整屏画面重画(那张画面本来就不含 WAIO 窗口,所以拖动跟手)。
    /// </summary>
    private static void Sync(GlassSurface surface, bool refresh)
    {
        if (!surface.IsVisible)
        {
            return;
        }

        try
        {
            var capture = CaptureFor(surface.Monitor);
            surface.Capture = capture;
            if (refresh || capture.Latest is null)
            {
                capture.Request();
            }
        }
        catch (Exception ex)
        {
            LogFailure(ex, "取窗口背后的画面");
        }

        surface.Render();
    }

    /// <summary>轮询:刚变可见、换了显示器的窗口补一次同步;开着的窗口背景过期就重新取。</summary>
    private static void Poll()
    {
        foreach (var surface in Surfaces.ToList())
        {
            var visible = surface.IsVisible;
            var justShown = visible && !surface.WasVisible;
            surface.WasVisible = visible;
            if (!visible)
            {
                continue;
            }

            // 刚显示出来:显示事件可能早于窗口真正可见(那时画不上),这里补画一次
            if (justShown || surface.Capture is null || surface.Capture.Monitor != surface.Monitor
                || (!surface.Capture.IsBusy && DateTime.UtcNow - surface.Capture.CapturedAt > RefreshInterval))
            {
                Sync(surface, refresh: true);
            }
        }
    }

    private static GlassCapture CaptureFor(nint monitor)
    {
        if (!Captures.TryGetValue(monitor, out var capture))
        {
            _device ??= CanvasDevice.GetSharedDevice();
            capture = new GlassCapture(monitor, _device, _ui!, () => Surfaces.Where(s => s.IsVisible).Select(s => s.Hwnd).ToList(), OnFrameReady);
            Captures[monitor] = capture;
        }

        return capture;
    }

    /// <summary>新的一帧到了:重画这块屏上所有可见的玻璃。</summary>
    private static void OnFrameReady(GlassCapture capture)
    {
        LogBorderOnce(capture);
        foreach (var surface in Surfaces)
        {
            if (surface.Capture == capture)
            {
                surface.Render();
            }
        }
    }

    private static void LogBorderOnce(GlassCapture capture)
    {
        if (_loggedBorder)
        {
            return;
        }

        _loggedBorder = true;
        _logger?.LogInformation("外观:第一次取到窗口背后的画面(关闭截屏黄框:HRESULT 0x{Hr:X8})", capture.BorderResult ?? 0);
    }

    /// <summary>系统强调色(没有自定义强调色时用它算选中胶囊等淡色)。</summary>
    private static Color SystemAccent()
    {
        try
        {
            return new UISettings().GetColorValue(UIColorType.Accent);
        }
        catch (Exception)
        {
            return Color.FromArgb(0xFF, 0x00, 0x67, 0xC0); // Windows 默认蓝
        }
    }
}
