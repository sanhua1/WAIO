using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>贴图(spec F5、F12)和重新编辑标注(MAIO ScreenshotController+Pins)。</summary>
internal sealed partial class ScreenshotController
{
    /// <summary>贴图管理(第一次用时创建;UI 线程)。</summary>
    private PinManager Pins
    {
        get
        {
            if (_pins is null)
            {
                _pins = new PinManager(() => Settings.PinControls());
                _pins.ActionRequested += Handle;
                _pins.Pinned += RecognizePinText;
            }

            return _pins;
        }
    }

    /// <summary>编辑没能开始时,把藏起来的贴图显示回来(MAIO restoreEditingPin)。</summary>
    private void RestoreEditingPin()
    {
        _editing?.Pin?.Show();
        _editing = null;
    }

    // ---- 命令 ----

    /// <summary>把剪贴板里的图片(或文字渲染成的图片)贴到屏幕上,写入历史;内容本来就在剪贴板上,不再复制(MAIO pinClipboard)。</summary>
    public async Task PinClipboardAsync()
    {
        var clipboard = await context.Clipboard.GetAsync().ConfigureAwait(false);
        var display = DisplayAtCursor();
        var content = clipboard is null ? null : await ClipboardImage.ReadAsync(clipboard, display?.Scale ?? 1).ConfigureAwait(false);
        if (content is null)
        {
            await context.Overlay.ShowHudAsync("剪贴板里没有图片或文字").ConfigureAwait(false);
            return;
        }

        var scale = display?.Scale ?? 1;
        var pin = await OnUiAsync(() => PinCentered(content.Image, scale, null)).ConfigureAwait(false);
        var result = new CaptureResult(content.Image, scale, pin.ContentRect);
        var id = await OutputAsync(result, CaptureAction.Pin, context.Events.Foreground?.ProcessName, copies: false).ConfigureAwait(false);
        if (id is not null)
        {
            context.Ui.Post(() => pin.RecordId = id);
        }
    }

    /// <summary>重新贴最近一张截图(MAIO pinLast)。</summary>
    public async Task PinLastAsync()
    {
        var records = history is null ? [] : await history.RecordsAsync().ConfigureAwait(false);
        if (records.Count == 0)
        {
            await context.Overlay.ShowHudAsync("还没有截图历史").ConfigureAwait(false);
            return;
        }

        await PinRecordAsync(records[0].Id).ConfigureAwait(false);
    }

    /// <summary>从历史贴图,同时复制(spec:贴图时自动复制,MAIO pinRecord)。</summary>
    public async Task PinRecordAsync(string id)
    {
        if (await LoadFinalImageAsync(id).ConfigureAwait(false) is not ({ } record, { } image))
        {
            return;
        }

        var scale = record.Scale ?? DisplayAtCursor()?.Scale ?? 1;
        await OnUiAsync(() => PinCentered(image, scale, id)).ConfigureAwait(false);
        await CopyImageAsync(image, scale).ConfigureAwait(false);
    }

    public Task CloseAllPinsAsync() => OnUiAsync(() => _pins?.CloseAll());

    /// <summary>打开截图历史面板(MAIO showHistory)。</summary>
    public Task ShowHistoryAsync() => OnUiAsync(() =>
    {
        if (history is null)
        {
            _ = context.Overlay.ShowHudAsync("截图历史不可用(数据目录无法读写)");
            return;
        }

        if (_historyWindow is null)
        {
            var window = new HistoryWindow(history, new HistoryWindow.Actions(
                id => _ = PinRecordAsync(id),
                id => _ = CopyRecordAsync(id),
                id => _ = BeginEditAsync(id, null),
                id => _ = RevealAsync(history.Folder(id)),
                (id, translate) => _ = Task.Run(() => RecognizeRecordAsync(id, translate))));
            window.Closed += (_, _) => _historyWindow = null;
            _historyWindow = window;
        }

        _historyWindow.ShowAndActivate();
    });

    /// <summary>在资源管理器里打开这条记录的文件夹(经 ILauncher 降权启动,ADR-0006)。</summary>
    private async Task RevealAsync(string folder)
    {
        try
        {
            await context.Launcher.LaunchAsync(new Waio.Kit.Services.LaunchRequest(folder)).ConfigureAwait(false);
        }
        catch (Waio.Kit.Services.LaunchException ex)
        {
            await context.Overlay.ShowHudAsync(ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>复制历史里的一张(最终图)。</summary>
    private async Task CopyRecordAsync(string id)
    {
        if (await LoadFinalImageAsync(id).ConfigureAwait(false) is ({ } record, { } image)
            && await CopyImageAsync(image, record.Scale ?? 1).ConfigureAwait(false))
        {
            await context.Overlay.ShowHudAsync("已复制截图").ConfigureAwait(false);
        }
    }

    /// <summary>没有原始位置时放在鼠标所在显示器的中间,太大时缩小(MAIO PinManager.centeredRect)。</summary>
    private PinWindow PinCentered(PixelBuffer image, double scale, string? recordId)
    {
        var bounds = DisplayAtCursor()?.Bounds ?? new PxRect(0, 0, 1920, 1080);
        var (origin, zoom) = PinState.Centered((image.Width, image.Height), bounds);
        return Pins.Pin(image, scale, origin, zoom, recordId, Settings.PinShadow);
    }

    private static DisplayInfo? DisplayAtCursor()
    {
        var cursor = ScreenLocator.CursorPosition();
        var displays = ScreenCapturer.Displays();
        return displays.FirstOrDefault(d => d.Bounds.Contains(new PxPoint(cursor.X, cursor.Y))) ?? displays.FirstOrDefault();
    }

    private async Task<(ScreenshotRecord Record, PixelBuffer Image)?> LoadFinalImageAsync(string id)
    {
        if (history is null || await history.RecordAsync(id).ConfigureAwait(false) is not { } record)
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(history.FinalImagePath(record)).ConfigureAwait(false);
            return (record, await ImageCodec.DecodeAsync(bytes).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            context.Logger.LogWarning(ex, "读取截图历史失败:{Id}", id);
            await context.Overlay.ShowHudAsync("这张截图的文件读不了,可能已被删除").ConfigureAwait(false);
            return null;
        }
    }

    // ---- 贴图窗口的操作(UI 线程) ----

    private void Handle(PinWindow pin, PinAction action)
    {
        switch (action)
        {
            case PinAction.Copy:
                var copied = pin.OutputImage;
                var scale = pin.State.Scale;
                _ = Task.Run(async () =>
                {
                    if (await CopyImageAsync(copied, scale).ConfigureAwait(false))
                    {
                        await context.Overlay.ShowHudAsync("已复制贴图").ConfigureAwait(false);
                    }
                });
                break;
            case PinAction.Save:
                var saved = pin.OutputImage;
                var saveScale = pin.State.Scale;
                _ = Task.Run(async () =>
                {
                    var png = await ImageCodec.EncodePngAsync(saved, saveScale).ConfigureAwait(false);
                    await SaveAsync(png, DateTimeOffset.Now, Settings.AskSaveLocation, announce: true).ConfigureAwait(false);
                });
                break;
            case PinAction.Edit when pin.RecordId is { } recordId:
                _ = BeginEditAsync(recordId, pin);
                break;
            case PinAction.Ocr or PinAction.Translate:
                // 用贴图当前看到的样子(含旋转);识别结果记到这条历史
                var shown = pin.OutputImage;
                var record = pin.RecordId;
                _ = Task.Run(() => RecognizeAsync(shown, record, action == PinAction.Translate));
                break;
            case PinAction.CopyText or PinAction.CopyAllText:
                var text = action == PinAction.CopyText ? pin.SelectedText : pin.Text?.AllText ?? "";
                if (text.Length > 0)
                {
                    _ = CopyTextAsync(text);
                }

                break;
            case PinAction.Close:
                Pins.Close(pin);
                break;
            case PinAction.CloseAll:
                Pins.CloseAll();
                break;
        }
    }

    // ---- 重新编辑 ----

    /// <summary>
    /// 打开已有截图的标注编辑(MAIO beginEdit):从贴图打开时就在贴图的位置编辑(先把贴图藏起来,免得被截进背景),否则放在屏幕中间。
    /// 可以在任意线程调用。
    /// </summary>
    public async Task BeginEditAsync(string recordId, PinWindow? pin)
    {
        if (history is null || await history.RecordAsync(recordId).ConfigureAwait(false) is not { } record)
        {
            return;
        }

        PixelBuffer original;
        try
        {
            original = await ImageCodec.DecodeAsync(await File.ReadAllBytesAsync(history.OriginalPath(recordId)).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            context.Logger.LogWarning(ex, "读取截图原图失败:{Id}", recordId);
            await context.Overlay.ShowHudAsync("这张截图的原图读不了,不能编辑").ConfigureAwait(false);
            return;
        }

        var stored = history.Annotations(recordId);
        var recordScale = record.Scale ?? 1;
        var (started, anchor) = await OnUiAsync(() =>
        {
            if (_session is not null || _editing is not null)
            {
                return (false, (PxPoint?)null);
            }

            _editing = new Editing(recordId, pin);
            if (pin is null)
            {
                return (true, null);
            }

            var rect = pin.ContentRect;
            pin.Hide();
            return (true, new PxPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        }).ConfigureAwait(false);
        if (started)
        {
            if (pin is not null)
            {
                await Task.Delay(60).ConfigureAwait(false); // 等 DWM 把藏起来的贴图从画面上去掉,再冻结屏幕
            }

            await StartAsync(CaptureAction.Copy, frozen => EditContextFor(original, recordScale, stored, anchor, frozen)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 选一个显示器放下原图(MAIO editContext):按 1:1 像素显示,放不下时缩小;标注从记录的坐标(相对原图的像素)换算成全局坐标。
    /// </summary>
    internal static EditContext? EditContextFor(
        PixelBuffer original, double recordScale, IReadOnlyList<Annotation> annotations, PxPoint? center, IReadOnlyList<FrozenDisplay> frozen)
    {
        var cursor = ScreenLocator.CursorPosition();
        var anchor = center ?? new PxPoint(cursor.X, cursor.Y);
        if ((frozen.FirstOrDefault(f => f.Display.Bounds.Contains(anchor)) ?? frozen.FirstOrDefault()) is not { } display)
        {
            return null;
        }

        var placement = EditPlacement.For((original.Width, original.Height), display.Display.Bounds, center);
        var origin = new PxPoint(placement.Rect.X, placement.Rect.Y);
        var source = new EffectSource(original, origin, recordScale / placement.Zoom, placement.Zoom);
        var items = annotations.Select(a => a.Transformed(new VectorD(0, 0), 1 / placement.Zoom).Moved(new VectorD(origin.X, origin.Y))).ToList();
        return new EditContext(source, placement.Rect, items);
    }
}
