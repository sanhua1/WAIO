using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Kit.Commands;
using Waio.Kit.Contracts;
using Waio.Kit.Services;

namespace Waio.Feature.Screenshot.UI;

/// <summary>识别文字和截图翻译(MAIO ScreenshotController+OCR,ADR-0018)。</summary>
internal sealed partial class ScreenshotController
{
    private readonly List<OcrResultWindow> _ocrWindows = []; // 只在 UI 线程访问

    /// <summary>
    /// 识别图片里的文字,写入这条历史(用于搜索)。<paramref name="translate"/> 为 true 时直接交给翻译窗口,否则打开结果窗口。
    /// 可以在任意线程调用。
    /// </summary>
    private async Task RecognizeAsync(PixelBuffer image, string? recordId, bool translate)
    {
        await context.Overlay.ShowHudAsync("正在识别文字…").ConfigureAwait(false);
        string text;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            (text, var fallback) = await Task.Run(() => TextRecognizer.RecognizeAsync(image)).ConfigureAwait(false);
            if (fallback is not null)
            {
                context.Logger.LogWarning("识别文字改用系统识别(英文、数字较差):{Reason}", fallback);
            }

            context.Logger.LogInformation("识别文字用时 {Elapsed} ms({Width}×{Height},{Engine},{Length} 字)",
                clock.ElapsedMilliseconds, image.Width, image.Height, fallback is null ? "PP-OCR" : "系统识别", text.Length);
        }
        catch (TextRecognitionException ex)
        {
            context.Logger.LogWarning("识别文字不可用:{Message}", ex.Message);
            await context.Overlay.ShowHudAsync(ex.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            context.Logger.LogError(ex, "识别文字失败");
            await context.Overlay.ShowHudAsync($"识别文字失败:{ex.Message}").ConfigureAwait(false);
            return;
        }

        if (text.Trim().Length == 0)
        {
            await context.Overlay.ShowHudAsync("没有识别到文字").ConfigureAwait(false);
            return;
        }

        if (recordId is not null && history is not null)
        {
            try
            {
                await history.SetOcrTextAsync(recordId, text).ConfigureAwait(false); // 历史窗口收到 Changed 后自己刷新
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Logger.LogWarning(ex, "写入识别文字失败:{Id}", recordId);
            }
        }

        if (translate)
        {
            await TranslateAsync(text).ConfigureAwait(false);
        }
        else
        {
            await OnUiAsync(() => ShowOcrResult(text)).ConfigureAwait(false);
        }
    }

    /// <summary>每次识别打开一个结果窗口(可以同时开几个对照,MAIO OCRResultWindows)。UI 线程。</summary>
    private void ShowOcrResult(string text)
    {
        var window = new OcrResultWindow(text, new OcrResultWindow.Actions(
            copied => _ = CopyTextAsync(copied),
            translated => _ = TranslateAsync(translated)));
        window.Closed += (_, _) => _ocrWindows.Remove(window);
        _ocrWindows.Add(window);
        window.ShowAndActivate();
    }

    private void CloseOcrWindows()
    {
        foreach (var window in _ocrWindows.ToList())
        {
            window.Close();
        }

        _ocrWindows.Clear();
    }

    private async Task CopyTextAsync(string text)
    {
        var copied = await context.Clipboard.CopyAsync(ClipboardContent.FromText(text)).ConfigureAwait(false);
        await context.Overlay.ShowHudAsync(copied ? "已复制文字" : "剪贴板被其他程序占用,没有复制成功").ConfigureAwait(false);
    }

    /// <summary>通过契约交给翻译功能(ARCHITECTURE §4.3;截图功能不能引用翻译功能)。</summary>
    private async Task TranslateAsync(string text)
    {
        try
        {
            await context.CommandBus.PerformAsync(TranslateContracts.TranslateText, new TranslateTextInput(text)).ConfigureAwait(false);
        }
        catch (CommandException ex) when (ex.Kind == CommandErrorKind.Unavailable)
        {
            await context.Overlay.ShowHudAsync("翻译功能没有启用:设置 → 翻译 → 打开").ConfigureAwait(false);
        }
        catch (CommandException ex)
        {
            context.Logger.LogError(ex, "打开翻译失败");
            await context.Overlay.ShowHudAsync($"打开翻译失败:{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 贴图后在后台识别文字,完成后交给贴图,鼠标就能在文字上选择(同 PixPin,UI 线程调用)。
    /// 识别结果顺便写进这条历史(可以按文字搜索)。关掉设置或没有模型时什么都不做,贴图照常可用。
    /// </summary>
    private void RecognizePinText(PinWindow pin)
    {
        if (!Settings.PinSelectableText)
        {
            return;
        }

        var image = pin.Image;
        _ = Task.Run(() =>
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                if (TextRecognizer.RecognizeSelectable(image) is not { } layout)
                {
                    context.Logger.LogInformation("贴图选择文字不可用:没有 PP-OCR 模型");
                    return;
                }

                context.Logger.LogInformation("贴图识别文字用时 {Elapsed} ms({Width}×{Height},{Count} 段)",
                    clock.ElapsedMilliseconds, image.Width, image.Height, layout.Segments.Count);
                context.Ui.Post(() =>
                {
                    pin.SetText(layout, image);
                    if (pin.RecordId is { } id && history is not null && !layout.IsEmpty)
                    {
                        _ = history.SetOcrTextAsync(id, layout.AllText);
                    }
                });
            }
            catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or DllNotFoundException or TypeInitializationException)
            {
                context.Logger.LogWarning(ex, "贴图识别文字失败");
            }
        });
    }

    /// <summary>历史 / 贴图:识别文字或翻译(MAIO recognizeRecord)。</summary>
    private async Task RecognizeRecordAsync(string id, bool translate)
    {
        if (await LoadFinalImageAsync(id).ConfigureAwait(false) is ({ }, { } image))
        {
            await RecognizeAsync(image, id, translate).ConfigureAwait(false);
        }
    }
}
