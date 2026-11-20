using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Waio.UI.Theming;

/// <summary>
/// 玻璃窗口"画好了再露面"(ADR-0027 补充)。
/// <para>
/// 窗口一显示,系统立刻把它放上屏幕;可玻璃的背景要现取(开截屏会话等第一帧,几十毫秒),
/// 这期间窗口只有一块不透明的底色,看起来就是"先黑一块再出来"。
/// 做法:窗口<b>正要显示</b>时(WM_WINDOWPOSCHANGING 带 SWP_SHOWWINDOW,此时还没上屏)用 DWM cloak 把它藏起来 →
/// 取背景、画玻璃 → 等 XAML 交出新的帧 → 取消 cloak。窗口藏着时不参与合成,取到的画面里本来就没有它。
/// </para>
/// 兜底:无论哪一步没回来,<see cref="Timeout"/> 后一定露出来(WinUI 对 cloak 中的窗口有时不渲染,不能无限等)。
/// 只在 UI 线程使用。
/// </summary>
internal sealed unsafe partial class GlassReveal
{
    /// <summary>最多藏这么久。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);

    /// <summary>玻璃画好后最多再等这么久 XAML 出帧(XAML 不出帧时不必等到总超时)。</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMilliseconds(60);

    /// <summary>玻璃画好后等 XAML 渲染这么多帧再露出来。</summary>
    private const int FramesAfterDraw = 2;

    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmNcDestroy = 0x0082;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const int DwmwaCloak = 13;
    private const nuint SubclassId = 0x57414F52; // "WAOR"

    private static readonly Dictionary<nint, GlassReveal> ByHwnd = [];

    private readonly nint _hwnd;
    private readonly DispatcherQueue _ui;
    private readonly Action _showing;
    private readonly ILogger? _logger;
    private readonly DispatcherQueueTimer _timeout;
    private readonly DispatcherQueueTimer _settle;
    private readonly Stopwatch _clock = new();
    private bool _drawn;
    private int _frames;

    /// <param name="showing">窗口刚被藏起来、正要显示时(排到 UI 线程稍后执行):开始取背景。</param>
    public GlassReveal(nint hwnd, DispatcherQueue ui, Action showing, ILogger? logger)
    {
        _hwnd = hwnd;
        _ui = ui;
        _showing = showing;
        _logger = logger;
        _timeout = CreateTimer(Timeout, () => Finish("超时"));
        _settle = CreateTimer(SettleTimeout, () => Finish("玻璃已画好"));

        ByHwnd[hwnd] = this;
        if (!SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0))
        {
            ByHwnd.Remove(hwnd);
            logger?.LogWarning("外观:挂窗口消息失败,这个窗口显示时可能先闪一下底色");
        }
    }

    /// <summary>正藏着等玻璃画好。</summary>
    public bool IsPending { get; private set; }

    /// <summary>玻璃已按新背景(或已有背景)画好:等 XAML 出两帧就露出来。</summary>
    public void Drawn()
    {
        if (!IsPending || _drawn)
        {
            return;
        }

        _drawn = true;
        _frames = 0;
        CompositionTarget.Rendering += OnRendering;
        _settle.Start();
    }

    /// <summary>露出来(取消 cloak)。没藏着时什么也不做。</summary>
    public void Finish(string reason)
    {
        if (!IsPending)
        {
            return;
        }

        IsPending = false;
        CompositionTarget.Rendering -= OnRendering;
        _timeout.Stop();
        _settle.Stop();
        _ = Cloak(false);
        _logger?.LogInformation("外观:玻璃窗口露出,用时 {Ms} ms({Reason})", _clock.ElapsedMilliseconds, reason);
    }

    private void Begin()
    {
        if (IsPending || !Cloak(true))
        {
            return;
        }

        IsPending = true;
        _drawn = false;
        _clock.Restart();
        _timeout.Start();
        if (!_ui.TryEnqueue(() => _showing()))
        {
            Finish("排队失败");
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (++_frames >= FramesAfterDraw)
        {
            Finish("XAML 已出帧");
        }
    }

    private bool Cloak(bool cloak)
    {
        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(_hwnd, DwmwaCloak, &value, sizeof(int)) == 0;
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = _ui.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    /// <summary>窗口消息钩子(原生回调,绝不能抛异常)。</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (msg == WmWindowPosChanging && lParam != 0 && ByHwnd.TryGetValue(hwnd, out var reveal))
            {
                var flags = ((WindowPos*)lParam)->Flags;
                if ((flags & SwpShowWindow) != 0 && !GlassNative.IsWindowVisible(hwnd))
                {
                    reveal.Begin();
                }
                else if ((flags & SwpHideWindow) != 0)
                {
                    reveal.Finish("隐藏"); // 藏着时又被隐藏:取消 cloak,免得下次显示还是看不见
                }
            }
            else if (msg == WmNcDestroy && ByHwnd.Remove(hwnd, out var gone))
            {
                gone.Finish("销毁");
                _ = RemoveWindowSubclass(hwnd, &SubclassProc, SubclassId);
            }
        }
        catch (Exception)
        {
            // 吞掉:异常穿过原生回调会直接结束进程
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Hwnd;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, void* value, int size);
}
