using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 长截图(MAIO ScreenshotController+Scrolling,ADR-0020):在选区上开始滚动拼接;结束后先写入历史(不会丢),
/// 再打开标注编辑,之后复制、保存、贴图和普通截图一样。
/// </summary>
internal sealed partial class ScreenshotController
{
    private ScrollCaptureSession? _scrollCapture; // 只在 UI 线程访问

    /// <summary>UI 线程。</summary>
    private void BeginScrollCapture(CaptureResult result, string? sourceApp)
    {
        if (_scrollCapture is not null)
        {
            return;
        }

        var frame = result.Frame;
        var monitor = ScreenLocator.MonitorAt(new ScreenPoint(frame.CenterX, frame.CenterY));
        var w = monitor.WorkArea;
        var work = PxRect.FromEdges(w.Left, w.Top, w.Right, w.Bottom);
        ScrollCaptureSession? capture = null;
        try
        {
            capture = new ScrollCaptureSession(frame, result.Scale, work, context.Input, context.Ui, context.Logger, image =>
            {
                _scrollCapture = null;
                if (image is null)
                {
                    _ = context.Overlay.ShowHudAsync("长截图失败:没有截到内容").AsTask();
                    return;
                }

                _ = Task.Run(() => FinishScrollCaptureAsync(image, result.Scale, frame, sourceApp));
            });
            _scrollCapture = capture;
            capture.Start();
        }
        catch (Exception ex)
        {
            capture?.Cancel();
            _scrollCapture = null;
            context.Logger.LogError(ex, "开始长截图失败");
            _ = context.Overlay.ShowHudAsync($"长截图失败:{ex.Message}").AsTask();
        }
    }

    private async Task FinishScrollCaptureAsync(PixelBuffer image, double scale, PxRect frame, string? sourceApp)
    {
        await context.Overlay.ShowHudAsync($"长截图 {image.Width} × {image.Height}").ConfigureAwait(false);
        var result = new CaptureResult(image, scale, frame with { Height = image.Height });

        // 先只写历史(不复制):接着打开标注编辑,在那里 ⏎ 才复制(MAIO 同)
        if (await OutputAsync(result, CaptureAction.Copy, sourceApp, copies: false).ConfigureAwait(false) is { } id)
        {
            await BeginEditAsync(id, null).ConfigureAwait(false);
        }
        else if (await CopyImageAsync(image, scale).ConfigureAwait(false))
        {
            // 历史不可用(或写入失败)就没法重新编辑:至少复制下来,不让用户白滚
            await context.Overlay.ShowHudAsync("已复制长截图(截图历史不可用,不能编辑)").ConfigureAwait(false);
        }
    }
}
