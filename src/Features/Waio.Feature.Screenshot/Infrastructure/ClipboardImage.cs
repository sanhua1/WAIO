using System.Buffers.Binary;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Waio.Kit.Services;
using Windows.Graphics.DirectX;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 把剪贴板内容变成可以贴的图片(MAIO PasteboardImage,spec F12):图片、图片文件,或者把文字渲染成图片。
/// </summary>
internal static class ClipboardImage
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico", ".heic"];

    /// <summary>贴的内容。<paramref name="IsText"/>:是不是由文字渲染出来的。</summary>
    public sealed record Content(PixelBuffer Image, bool IsText);

    /// <param name="scale">文字渲染的缩放比例(鼠标所在显示器)。</param>
    public static async Task<Content?> ReadAsync(ClipboardContent clipboard, double scale)
    {
        if (clipboard.Image is { Length: > 40 } dib && await TryDecodeAsync(ToBmp(dib)) is { } image)
        {
            return new Content(image, false);
        }

        foreach (var file in clipboard.Files)
        {
            if (await ReadFileAsync(file) is { } fromFile)
            {
                return fromFile;
            }
        }

        return string.IsNullOrWhiteSpace(clipboard.Text) ? null : new Content(RenderText(clipboard.Text, scale), true);
    }

    /// <summary>读取图片文件;不是图片或读不了时返回 null。</summary>
    public static async Task<Content?> ReadFileAsync(string path)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return await TryDecodeAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)) is { } image ? new Content(image, false) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>文字渲染成白底黑字的图片,最宽 640 pt,自动换行(MAIO render(text:))。</summary>
    public static PixelBuffer RenderText(string text, double scale)
    {
        var padding = (float)(12 * scale);
        var device = CanvasDevice.GetSharedDevice();
        using var format = new CanvasTextFormat
        {
            FontFamily = TextRendering.FontFamily, FontSize = (float)(14 * scale), WordWrapping = CanvasWordWrapping.Wrap,
        };
        using var layout = new CanvasTextLayout(device, text, format, (float)(640 * scale), 100_000);
        var bounds = layout.LayoutBounds;
        var width = Math.Max(1, (int)Math.Ceiling(Math.Min(bounds.Width, 640 * scale) + padding * 2));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Min(bounds.Height, 30_000) + padding * 2));
        using var target = new CanvasRenderTarget(device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(AnnotationRenderer.Rgba(255, 255, 255, 255));
            ds.DrawTextLayout(layout, padding, padding, AnnotationRenderer.Rgba(255, 0, 0, 0));
        }

        var bytes = target.GetPixelBytes();
        PixelBuffer.MakeOpaque(bytes);
        return new PixelBuffer(width, height, bytes);
    }

    /// <summary>CF_DIB 前面加 14 字节的文件头,变成 BMP 文件交给 WIC 解码。</summary>
    internal static byte[] ToBmp(byte[] dib)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var bitCount = BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        var masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS:信息头后面跟 3 个颜色掩码
        var palette = bitCount <= 8 ? (colorsUsed == 0 ? 1 << bitCount : colorsUsed) * 4 : colorsUsed * 4;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 14 + headerSize + masks + palette);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private static async Task<PixelBuffer?> TryDecodeAsync(byte[] data)
    {
        try
        {
            return await ImageCodec.DecodeAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }
}
