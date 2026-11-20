using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.UI;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.UI;

namespace Waio.Feature.Screenshot;

/// <summary>
/// 截图(严格参考 MAIO FeatureScreenshot,ADR-0015):Win+C 冻结屏幕后框选,自动识别窗口(按住 Ctrl 识别控件),可以取色;
/// 截图自动复制,并写入截图历史。本类只做接线与命令调度(MAIO ScreenshotFeature + ScreenshotCommands)。
/// 分阶段实现(同 MAIO spec 第 6 节),六个阶段都已完成(第 6 阶段录屏,ADR-0021)。
/// </summary>
/// <param name="prewarm">启用时预先创建遮罩窗口(单测里没有 WinUI 环境,传 false)。</param>
public sealed class ScreenshotFeature(IFeatureContext context, bool prewarm) : IFeature
{
    public ScreenshotFeature(IFeatureContext context)
        : this(context, prewarm: true)
    {
    }

    public static readonly CommandId Capture = "screenshot.capture";
    public static readonly CommandId FullScreen = "screenshot.fullScreen";
    public static readonly CommandId Window = "screenshot.window";
    public static readonly CommandId Ocr = "screenshot.ocr";
    public static readonly CommandId Translate = "screenshot.translate";
    public static readonly CommandId Scrolling = "screenshot.scrolling";
    public static readonly CommandId Record = "screenshot.record";
    public static readonly CommandId RecordFullScreen = "screenshot.recordFullScreen";
    public static readonly CommandId StopRecording = "screenshot.stopRecording";
    public static readonly CommandId PinClipboard = "screenshot.pinClipboard";
    public static readonly CommandId PinLast = "screenshot.pinLast";
    public static readonly CommandId History = "screenshot.history";
    public static readonly CommandId CloseAllPins = "screenshot.closeAllPins";

    public static FeatureManifest Manifest { get; } = new(
        "screenshot",
        "截图",
        "按 Win+C 冻结屏幕后框选,自动识别窗口(按住 Ctrl 识别控件),可以取色、标注、贴图、识别文字(Ctrl+O)、翻译(Ctrl+T)、长截图(Ctrl+L)、录屏(Ctrl+R,或直接按 Alt+F3);Ctrl+Alt+F2 直接录鼠标所在的整块屏幕(画面 + 系统声音 + 麦克风,再按一次结束并选择保留哪些声音)。截图自动复制,并保存在截图历史里;Alt+F2 把剪贴板内容贴到屏幕上。",
        "", // Segoe Fluent Icons: Camera
        Capability.ScreenCapture,
        [
            new CommandDescriptor(Capture, "截图"),
            new CommandDescriptor(FullScreen, "全屏截图"),
            new CommandDescriptor(Window, "窗口截图"),
            new CommandDescriptor(Ocr, "截图识别文字"),
            new CommandDescriptor(Translate, "截图翻译"),
            new CommandDescriptor(Scrolling, "长截图"),
            new CommandDescriptor(Record, "录屏"),
            new CommandDescriptor(RecordFullScreen, "全屏录屏(开始 / 结束)"),
            new CommandDescriptor(StopRecording, "结束录屏"),
            new CommandDescriptor(PinClipboard, "贴剪贴板内容"),
            new CommandDescriptor(PinLast, "重新贴最近一张截图"),
            new CommandDescriptor(History, "截图历史"),
            new CommandDescriptor(CloseAllPins, "关闭全部贴图"),
        ],
        [
            new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("win+c")), Capture),
            new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("alt+f2")), PinClipboard),
            new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("alt+f3")), Record),
            new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("ctrl+alt+f2")), RecordFullScreen),
        ]);

    public static FeatureDescriptor Descriptor { get; } = new(Manifest, ctx => new ScreenshotFeature(ctx));

    /// <summary>设置窗口里"截图"页的设置部分(ADR-0010)。</summary>
    public static FeatureSettingsPage SettingsPage { get; } = new(Manifest.Id, ScreenshotSettingsView.Create);

    private readonly List<IDisposable> _registrations = [];
    private ScreenshotController? _controller;
    private HistoryLimit _limit;

    public async ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        HistoryStore? history = null;
        try
        {
            history = HistoryStore.For(context.Storage.DataDirectory, context.Logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.LogError(ex, "截图历史不可用");
        }

        var controller = new ScreenshotController(context, history);
        _controller = controller;
        _registrations.Add(context.Commands.Register(Capture, (_, _) => new ValueTask(controller.CaptureAsync())));
        _registrations.Add(context.Commands.Register(FullScreen, (_, _) => new ValueTask(controller.CaptureFullScreenAsync())));
        _registrations.Add(context.Commands.Register(Window, (_, _) => new ValueTask(controller.CaptureFrontWindowAsync())));
        _registrations.Add(context.Commands.Register(Ocr, (_, _) => new ValueTask(controller.CaptureAsync(CaptureAction.Ocr))));
        _registrations.Add(context.Commands.Register(Translate, (_, _) => new ValueTask(controller.CaptureAsync(CaptureAction.Translate))));
        _registrations.Add(context.Commands.Register(Scrolling, (_, _) => new ValueTask(controller.CaptureAsync(CaptureAction.Scrolling))));
        _registrations.Add(context.Commands.Register(Record, (_, _) => new ValueTask(controller.RecordAsync())));
        _registrations.Add(context.Commands.Register(RecordFullScreen, (_, _) => new ValueTask(controller.RecordFullScreenAsync())));
        _registrations.Add(context.Commands.Register(StopRecording, (_, _) => new ValueTask(controller.StopRecordingAsync())));
        _registrations.Add(context.Commands.Register(PinClipboard, (_, _) => new ValueTask(controller.PinClipboardAsync())));
        _registrations.Add(context.Commands.Register(PinLast, (_, _) => new ValueTask(controller.PinLastAsync())));
        _registrations.Add(context.Commands.Register(History, (_, _) => new ValueTask(controller.ShowHistoryAsync())));
        _registrations.Add(context.Commands.Register(CloseAllPins, (_, _) => new ValueTask(controller.CloseAllPinsAsync())));

        // 修改历史上限后立即清理(MAIO settingsTask)
        _limit = context.Settings.Get<ScreenshotSettings>().HistoryLimit;
        context.Settings.Changed += OnSettingsChanged;
        if (prewarm)
        {
            await controller.PrepareAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DeactivateAsync()
    {
        context.Settings.Changed -= OnSettingsChanged;
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        if (_controller is { } controller)
        {
            _controller = null;
            await controller.TearDownAsync().ConfigureAwait(false);
        }
    }

    private void OnSettingsChanged()
    {
        var limit = context.Settings.Get<ScreenshotSettings>().HistoryLimit;
        if (limit != _limit)
        {
            _limit = limit;
            _controller?.EnforceHistoryLimit();
        }
    }
}
