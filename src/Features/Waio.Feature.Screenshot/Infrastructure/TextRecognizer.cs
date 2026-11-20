using System.Runtime.InteropServices.WindowsRuntime;
using Waio.Feature.Screenshot.Domain;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>没有可用的识别引擎等原因导致不能识别。消息直接显示给用户。</summary>
internal sealed class TextRecognitionException(string message) : Exception(message);

/// <summary>
/// 识别文字(对应 MAIO TextRecognizer)。主引擎是 PP-OCRv5(<see cref="PaddleOcr"/>,ADR-0019);
/// 模型文件缺失或加载失败时退回系统自带的 <c>Windows.Media.Ocr</c>(ADR-0018,英文、数字明显更差)。
/// 都是离线的。CPU 密集,在线程池上调用。
/// </summary>
internal static class TextRecognizer
{
    /// <summary>识别并拼成文字。<paramref name="fallbackReason"/> 不为 null 时表示用了系统识别,以及原因(写日志用)。</summary>
    public static async Task<(string Text, string? FallbackReason)> RecognizeAsync(PixelBuffer image)
    {
        string reason;
        if (PaddleOcr.ModelsPresent)
        {
            try
            {
                return (OcrLayout.Text(PaddleOcr.Shared.Recognize(image).Select(s => new RecognizedLine(s.Text, s.Box))), null);
            }
            catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or DllNotFoundException or TypeInitializationException)
            {
                reason = $"文字识别失败:{ex.Message}";
            }
        }
        else
        {
            reason = $"没有找到文字识别模型({PaddleOcr.ModelDirectory})";
        }

        return (await RecognizeWithWindowsAsync(image), reason);
    }

    /// <summary>
    /// 贴图上可以选择的文字(需要逐字位置,只有 PP-OCR 给得出;模型缺失时返回 null,贴图照常可用,只是不能选字)。
    /// CPU 密集,在线程池上调用。
    /// </summary>
    public static PinTextLayout? RecognizeSelectable(PixelBuffer image) =>
        PaddleOcr.ModelsPresent ? new PinTextLayout(PaddleOcr.Shared.Recognize(image)) : null;

    /// <summary>系统自带的识别(用户语言列表里第一个装了识别组件的语言)。</summary>
    internal static async Task<string> RecognizeWithWindowsAsync(PixelBuffer image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new TextRecognitionException("没有可用的文字识别引擎:识别模型缺失,系统也没有装\"光学字符识别\"语言组件");

        // 只在超过系统上限时缩小。真实截图上放大反而更差(ADR-0019 的实验:150% 截图放大 2 倍错字翻倍)
        var max = (int)OcrEngine.MaxImageDimension;
        var factor = Math.Max(image.Width, image.Height) > max ? (double)max / Math.Max(image.Width, image.Height) : 1;
        var input = factor == 1 ? image : Resize(image, factor);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            input.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, input.Width, input.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        var lines = result.Lines.Select(line => OcrLayout.JoinWords([.. line.Words.Select(word =>
        {
            var r = word.BoundingRect;
            return new RecognizedWord(word.Text, new TextBounds(r.X / factor, r.Y / factor, r.Width / factor, r.Height / factor));
        })]));
        return OcrLayout.Text(lines);
    }

    /// <summary>双线性缩放(只给识别用,不追求画质)。</summary>
    internal static PixelBuffer Resize(PixelBuffer source, double factor)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * factor));
        var height = Math.Max(1, (int)Math.Round(source.Height * factor));
        var pixels = new byte[width * height * 4];
        var src = source.Pixels;
        var stride = source.Stride;
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) / factor - 0.5, 0, source.Height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) / factor - 0.5, 0, source.Width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var fx = sx - x0;
                var target = (y * width + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var top = src[y0 * stride + x0 * 4 + c] * (1 - fx) + src[y0 * stride + x1 * 4 + c] * fx;
                    var bottom = src[y1 * stride + x0 * 4 + c] * (1 - fx) + src[y1 * stride + x1 * 4 + c] * fx;
                    pixels[target + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                }
            }
        }

        return new PixelBuffer(width, height, pixels);
    }
}
