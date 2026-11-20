using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Kit.Features;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 截图的总调度(MAIO ScreenshotController):冻结屏幕、复用遮罩窗口、开始截图会话;输出(复制、保存、写历史)见 <c>.Output.cs</c>。
/// 命令在线程池上调用;遮罩和会话只在 UI 线程上碰。
/// </summary>
internal sealed partial class ScreenshotController(IFeatureContext context, HistoryStore? history)
{
    private readonly List<OverlayWindow> _overlays = []; // 只在 UI 线程访问
    private CaptureSession? _session; // 只在 UI 线程访问
    private PinManager? _pins; // 只在 UI 线程访问
    private HistoryWindow? _historyWindow; // 只在 UI 线程访问
    private Editing? _editing; // 只在 UI 线程访问
    private int _starting;

    /// <summary>正在重新编辑的历史记录;从贴图打开时还有那张贴图(编辑完替换它)。</summary>
    private sealed record Editing(string RecordId, PinWindow? Pin);

    private ScreenshotSettings Settings => context.Settings.Get<ScreenshotSettings>();

    /// <summary>启用时预先创建遮罩窗口(MAIO spec 5.3:按下快捷键后只做显示),并按上限清理历史。</summary>
    public Task PrepareAsync() => OnUiAsync(() =>
    {
        foreach (var display in ScreenCapturer.Displays())
        {
            _overlays.Add(new OverlayWindow(display)); // 创建后停在屏幕外显示着,截图时直接挪过来
        }

        EnforceHistoryLimit();
    });

    public Task TearDownAsync() => OnUiAsync(() =>
    {
        _session?.Finish(new CaptureOutcome.Cancelled());
        _session = null;
        _scrollCapture?.Cancel();
        _scrollCapture = null;
        _recording?.Abort();
        _fullScreenRecording?.Abort();
        _fullScreenRecording = null;
        _recording = null;
        _pins?.Dispose();
        _pins = null;
        _historyWindow?.Close();
        _historyWindow = null;
        CloseOcrWindows();
        foreach (var overlay in _overlays)
        {
            overlay.Close();
        }

        _overlays.Clear();
    });

    /// <summary>区域截图:先冻结所有显示器和窗口列表,再显示遮罩(ADR-0011)。</summary>
    public Task CaptureAsync(CaptureAction primaryAction = CaptureAction.Copy) => StartAsync(primaryAction, null);

    /// <summary>冻结屏幕并开始交互;<paramref name="edit"/> 不为 null 时是重新编辑(MAIO startSession)。</summary>
    private async Task StartAsync(CaptureAction primaryAction, Func<IReadOnlyList<FrozenDisplay>, EditContext?>? edit)
    {
        if (Interlocked.Exchange(ref _starting, 1) == 1)
        {
            await OnUiAsync(RestoreEditingPin).ConfigureAwait(false);
            return;
        }

        try
        {
            // 录制中可以截图;正在选录屏格式时不行(MAIO 同)
            if (await OnUiAsync(() => _session is not null || _scrollCapture is not null || _recording is { IsSettingUp: true }).ConfigureAwait(false))
            {
                await OnUiAsync(RestoreEditingPin).ConfigureAwait(false);
                return;
            }

            var clock = Stopwatch.StartNew();
            var previous = ScreenLocator.ForegroundWindow();
            var sourceApp = context.Events.Foreground?.ProcessName;
            var displays = ScreenCapturer.Displays();
            IReadOnlyList<WindowInfo> windows;
            IReadOnlyList<FrozenDisplay> frozen;
            try
            {
                // 自绘玻璃取背景时会短暂把 WAIO 窗口排除在截屏外;冻结屏幕这一下要保证 WAIO 窗口在画面里。
                // 窗口列表也在这里读:否则正好赶上取背景时,WAIO 窗口会因"排除在截屏外"被当成不可选
                using (Waio.UI.Theming.Glass.SuspendBackgroundRefresh())
                {
                    windows = WindowPicker.Candidates(WindowSnapshot.Current());
                    frozen = ScreenCapturer.Capture(displays);
                }
            }
            catch (CaptureException ex)
            {
                await ReportAsync(ex).ConfigureAwait(false);
                await OnUiAsync(RestoreEditingPin).ConfigureAwait(false);
                return;
            }

            var captured = clock.ElapsedMilliseconds;
            await OnUiAsync(() => StartSessionAsync(frozen, windows, primaryAction, previous, sourceApp, edit)).ConfigureAwait(false);
            context.Logger.LogInformation("截图遮罩出现用时 {Total} ms(其中截屏 {Capture} ms,{Count} 个显示器)",
                clock.ElapsedMilliseconds, captured, frozen.Count);
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    private async Task StartSessionAsync(
        IReadOnlyList<FrozenDisplay> frozen, IReadOnlyList<WindowInfo> windows, CaptureAction primaryAction, nint previous, string? sourceApp,
        Func<IReadOnlyList<FrozenDisplay>, EditContext?>? edit)
    {
        var editContext = _session is null && frozen.Count > 0 ? edit?.Invoke(frozen) : null;
        if (_session is not null || frozen.Count == 0 || (edit is not null && editContext is null))
        {
            RestoreEditingPin();
            return;
        }

        var settings = Settings;
        var options = new CaptureSession.Options(settings.AlwaysDetectControls, settings.ShowMagnifier, settings.ColorFormat)
        {
            PrimaryAction = primaryAction,
            ToolStyle = settings.ToolStyle,
        };
        var session = new CaptureSession(frozen, OverlaysFor(frozen), windows, options, new CaptureSession.Callbacks(
            format => UpdateSettings(s => s.ColorFormat = format),
            style => UpdateSettings(s => s.ToolStyle = style),
            outcome =>
            {
                _session = null;
                Complete(outcome, previous, sourceApp);
            }), editContext);
        _session = session;
        await session.ShowAsync();
    }

    /// <summary>每个冻结的显示器配一个遮罩;显示器的位置或缩放变了就挪过去(MAIO panel(for:))。</summary>
    private List<OverlayWindow> OverlaysFor(IReadOnlyList<FrozenDisplay> frozen)
    {
        while (_overlays.Count < frozen.Count)
        {
            _overlays.Add(new OverlayWindow(frozen[_overlays.Count].Display));
        }

        for (var i = 0; i < frozen.Count; i++)
        {
            if (_overlays[i].Display != frozen[i].Display)
            {
                _overlays[i].Place(frozen[i].Display);
            }
        }

        return _overlays.Take(frozen.Count).ToList();
    }

    private void UpdateSettings(Action<ScreenshotSettings> change)
    {
        var settings = Settings;
        change(settings);
        _ = context.Settings.SaveAsync(settings).AsTask();
    }

    private async Task ReportAsync(Exception error)
    {
        context.Logger.LogError(error, "截图失败");
        await context.Overlay.ShowHudAsync($"截图失败:{error.Message}").ConfigureAwait(false);
    }

    private Task OnUiAsync(Action action) => OnUiAsync(() =>
    {
        action();
        return true;
    });

    private Task OnUiAsync(Func<Task> action) => OnUiAsync<Task>(action).Unwrap();

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        if (context.Ui.IsCurrent)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = context.Ui.Post(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        if (!posted)
        {
            completion.TrySetCanceled();
        }

        return completion.Task;
    }
}
