using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 截图输出(MAIO ScreenshotController 的 complete / output / save,以及 +Commands 的全屏、窗口截图):
/// 自动复制 → 需要时保存文件 → 写入历史。复制用 <see cref="IClipboardService.CopyAsync"/>,剪贴板历史会记录(MAIO 同)。
/// </summary>
internal sealed partial class ScreenshotController
{
    /// <summary>截图会话结束(UI 线程,MAIO complete)。</summary>
    private void Complete(CaptureOutcome outcome, nint previous, string? sourceApp)
    {
        var editing = _editing;
        _editing = null;
        switch (outcome)
        {
            case CaptureOutcome.Cancelled:
                editing?.Pin?.Show();
                Restore(previous);
                break;
            case CaptureOutcome.Color color:
                editing?.Pin?.Show();
                Restore(previous);
                _ = Task.Run(async () =>
                {
                    await context.Clipboard.CopyAsync(ClipboardContent.FromText(color.Text)).ConfigureAwait(false);
                    await context.Overlay.ShowHudAsync($"已复制颜色 {color.Text}").ConfigureAwait(false);
                });
                break;
            case CaptureOutcome.Image { Action: CaptureAction.Scrolling } scrolling:
                editing?.Pin?.Show();
                Restore(previous); // 滚轮要落在原来的程序上
                BeginScrollCapture(scrolling.Result, sourceApp);
                break;
            case CaptureOutcome.Image { Action: CaptureAction.Record } recording:
                editing?.Pin?.Show();
                Restore(previous);
                BeginRecording(recording.Result);
                break;
            case CaptureOutcome.Image image:
                var asksLocation = image.Action == CaptureAction.Save && Settings.AskSaveLocation;
                var recognizes = image.Action.RecognizesText();
                if (!asksLocation && !recognizes) // 识别文字时接着会打开结果窗口或翻译窗口,不切回原程序(MAIO 同)
                {
                    Restore(previous);
                }

                // 贴图马上出现(MAIO 在写完历史后才贴;这里先贴,写完历史再把记录 id 交给它)
                var pin = FinishPinning(image.Result, image.Action, editing);
                _ = Task.Run(async () =>
                {
                    var id = await OutputAsync(image.Result, image.Action, sourceApp, asksLocation, editing?.RecordId, copies: !recognizes)
                        .ConfigureAwait(false);
                    if (pin is not null && id is not null)
                    {
                        context.Ui.Post(() => pin.RecordId = id);
                    }

                    if (asksLocation)
                    {
                        Restore(previous);
                    }

                    if (recognizes)
                    {
                        await RecognizeAsync(image.Result.FinalImage, id, image.Action == CaptureAction.Translate).ConfigureAwait(false);
                    }
                });
                break;
        }
    }

    /// <summary>Ctrl+P 在截图的位置原地贴出新图;从贴图打开的编辑,把那张贴图换成新图(MAIO finishPinning)。</summary>
    private PinWindow? FinishPinning(CaptureResult result, CaptureAction action, Editing? editing)
    {
        if (editing?.Pin is { } existing)
        {
            existing.Replace(result.FinalImage, result.Scale);
            RecognizePinText(existing); // 换了图,重新识别可选择的文字
            existing.Show();
            return null; // 记录 id 不变
        }

        if (action != CaptureAction.Pin)
        {
            return null;
        }

        return Pins.Pin(result.FinalImage, result.Scale, new PxPoint(result.Frame.X, result.Frame.Y),
            result.Frame.Width / (double)result.FinalImage.Width, editing?.RecordId, Settings.PinShadow);
    }

    /// <summary>
    /// 自动复制 → 需要时保存文件 → 写入(或更新)历史(MAIO output)。在后台线程上运行。返回历史记录 id。
    /// <paramref name="updating"/>:重新编辑时更新这条记录而不是新建;<paramref name="copies"/>:贴剪贴板内容时不再复制。
    /// </summary>
    private async Task<string?> OutputAsync(
        CaptureResult result, CaptureAction action, string? sourceApp, bool asksLocation = false, string? updating = null, bool copies = true)
    {
        try
        {
            // 复制、保存用最终图(有标注时是带标注的那张);历史里另存原图和标注,以后可以重新编辑(MAIO EncodedCapture)
            var original = await ImageCodec.EncodePngAsync(result.Original, result.Scale).ConfigureAwait(false);
            var png = result.Rendered is { } rendered ? await ImageCodec.EncodePngAsync(rendered, result.Scale).ConfigureAwait(false) : original;
            if (copies)
            {
                await CopyImageAsync(result.FinalImage, result.Scale).ConfigureAwait(false);
            }

            var date = DateTimeOffset.Now;
            var settings = Settings;
            if (action == CaptureAction.Save || (settings.AutoSave && copies))
            {
                await SaveAsync(png, date, asksLocation, announce: action == CaptureAction.Save).ConfigureAwait(false);
            }

            return await RecordAsync(result, original, result.Rendered is null ? null : png, sourceApp, date, updating).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReportAsync(ex).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>复制图片(DIB,剪贴板历史会记录)。返回是否成功。</summary>
    private async Task<bool> CopyImageAsync(PixelBuffer image, double scale)
    {
        if (await context.Clipboard.CopyAsync(new ClipboardContent { Image = ImageCodec.ToDib(image, scale) }).ConfigureAwait(false))
        {
            return true;
        }

        await context.Overlay.ShowHudAsync("剪贴板被其他程序占用,没有复制成功").ConfigureAwait(false);
        return false;
    }

    private async Task SaveAsync(byte[] png, DateTimeOffset date, bool asksLocation, bool announce)
    {
        var directory = ImageExporter.Directory(Settings.SaveDirectory);
        try
        {
            string? path;
            if (asksLocation)
            {
                path = await StaThread.RunAsync(() => ImageExporter.AskSaveLocation(0, directory, date)).ConfigureAwait(false);
                if (path is null)
                {
                    return;
                }

                await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);
            }
            else
            {
                path = ImageExporter.Save(png, directory, date);
            }

            if (announce)
            {
                await context.Overlay.ShowHudAsync($"已保存:{Path.GetFileName(path)}").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            context.Logger.LogError(ex, "保存截图失败");
            await context.Overlay.ShowHudAsync($"保存截图失败:{ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task<string?> RecordAsync(
        CaptureResult result, byte[] original, byte[]? rendered, string? sourceApp, DateTimeOffset date, string? updating)
    {
        if (history is null)
        {
            return null;
        }

        try
        {
            // 缩略图用最终图
            var thumbnail = await ImageCodec.EncodePngAsync(result.FinalImage, result.Scale, HistoryStore.ThumbnailPixels).ConfigureAwait(false);
            var annotations = result.Annotations.Count == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(result.Annotations);
            if (updating is not null)
            {
                await history.UpdateAsync(updating, rendered, annotations, thumbnail).ConfigureAwait(false);
                return updating;
            }

            var entry = new HistoryStore.Entry(original, thumbnail, result.Original.Width, result.Original.Height, result.Scale)
            {
                Rendered = rendered,
                Annotations = annotations,
            };
            var record = await history.AddAsync(entry, sourceApp, date).ConfigureAwait(false);
            await history.EnforceAsync(Settings.HistoryLimit.Bytes()).ConfigureAwait(false);
            return record.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.LogError(ex, "写入截图历史失败");
            return null;
        }
    }

    public void EnforceHistoryLimit()
    {
        if (history is not null)
        {
            var limit = Settings.HistoryLimit.Bytes();
            _ = Task.Run(() => history.EnforceAsync(limit));
        }
    }

    /// <summary>全屏截图:截取鼠标所在的显示器,直接复制并写入历史(MAIO captureFullScreen)。</summary>
    public async Task CaptureFullScreenAsync()
    {
        var cursor = ScreenLocator.CursorPosition();
        var displays = ScreenCapturer.Displays();
        var display = displays.FirstOrDefault(d => d.Bounds.Contains(new PxPoint(cursor.X, cursor.Y))) ?? displays.FirstOrDefault();
        if (display is null)
        {
            return;
        }

        try
        {
            var image = ScreenCapturer.Capture(display.Bounds);
            await OutputAsync(new CaptureResult(image, display.Scale, display.Bounds), CaptureAction.Copy, context.Events.Foreground?.ProcessName)
                .ConfigureAwait(false);
            await context.Overlay.ShowHudAsync("已复制全屏截图").ConfigureAwait(false);
        }
        catch (CaptureException ex)
        {
            await ReportAsync(ex).ConfigureAwait(false);
        }
    }

    /// <summary>窗口截图:截取最前面的窗口(不带阴影,被挡住的部分也能截到),直接复制并写入历史(MAIO captureFrontWindow)。</summary>
    public async Task CaptureFrontWindowAsync()
    {
        try
        {
            if (CaptureForegroundWindow() is not var (window, image))
            {
                await context.Overlay.ShowHudAsync("没有可以截取的窗口").ConfigureAwait(false);
                return;
            }

            var scale = ScreenCapturer.Displays().FirstOrDefault(d => d.Bounds.Intersects(window.Frame))?.Scale ?? 1;
            await OutputAsync(new CaptureResult(image, scale, window.Frame), CaptureAction.Copy, context.Events.Foreground?.ProcessName)
                .ConfigureAwait(false);
            await context.Overlay.ShowHudAsync("已复制窗口截图").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CaptureException or ArgumentException)
        {
            await ReportAsync(ex).ConfigureAwait(false);
        }
    }

    /// <summary>截最前面的窗口;没有可截的(或它不出现在截屏里)返回 null。</summary>
    private static (WindowInfo Window, PixelBuffer Image)? CaptureForegroundWindow()
    {
        // 自绘玻璃取背景时会短暂把 WAIO 窗口排除在截屏外;暂停它,WAIO 自己的窗口(设置、启动台)也截得到
        using var _ = Waio.UI.Theming.Glass.SuspendBackgroundRefresh();
        var foreground = ScreenLocator.ForegroundWindow();
        if (foreground == 0 || WindowSnapshot.Describe(foreground) is not { } window || window.ExcludedFromCapture)
        {
            return null;
        }

        return (window, ScreenCapturer.CaptureWindow(window.Handle, window.Frame));
    }

    private static void Restore(nint previous)
    {
        if (previous != 0)
        {
            _ = WindowChrome.RestoreForegroundAsync(previous, TimeSpan.FromMilliseconds(500));
        }
    }
}

/// <summary>在一个临时 STA 线程上运行(经典的"另存为"对话框要求 STA 并自带消息循环)。</summary>
internal static class StaThread
{
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "WAIO.Screenshot.SaveDialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
