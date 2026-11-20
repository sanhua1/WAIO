using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Windows.Graphics.DirectX;

namespace Waio.Feature.Screenshot.Infrastructure.Rendering;

/// <summary>
/// 冻结画面的来源:用来画底图、马赛克和模糊(MAIO EffectSource)。
/// <paramref name="origin"/> 是这张图左上角的全局坐标(像素);<paramref name="scale"/> 是缩放比例(文字内边距等按 pt 换算用);
/// <paramref name="zoom"/> 是每个屏幕像素对应几个图片像素:截图时是 1;重新编辑放不下的大图时大于 1(缩小显示,MAIO editContext)。
/// </summary>
internal sealed class EffectSource(PixelBuffer image, PxPoint origin, double scale, double zoom = 1) : IDisposable
{
    private CanvasBitmap? _bitmap;

    public PixelBuffer Image { get; } = image;

    public PxPoint Origin { get; } = origin;

    public double Scale { get; } = scale;

    public double Zoom { get; } = zoom;

    /// <summary>这张图在屏幕上占的位置(全局像素)。</summary>
    public RectD Bounds => new(Origin.X, Origin.Y, Image.Width / Zoom, Image.Height / Zoom);

    /// <summary>图片里的像素矩形在屏幕上的位置(全局像素)。</summary>
    public RectD GlobalRect(PxRect pixels) =>
        new(Origin.X + pixels.X / Zoom, Origin.Y + pixels.Y / Zoom, pixels.Width / Zoom, pixels.Height / Zoom);

    /// <summary>整张图上传成 GPU 位图(第一次用时;4K 约 33 MB)。</summary>
    public CanvasBitmap Bitmap(ICanvasResourceCreator device) =>
        _bitmap ??= CanvasBitmap.CreateFromBytes(device, Image.Pixels, Image.Width, Image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);

    /// <summary>全局矩形在这张图里的像素矩形(已裁到图片范围内,按像素向外取整)。</summary>
    public PxRect PixelRect(RectD rect) =>
        new RectD((rect.X - Origin.X) * Zoom, (rect.Y - Origin.Y) * Zoom, rect.Width * Zoom, rect.Height * Zoom).ToPx()
            .Intersect(new PxRect(0, 0, Image.Width, Image.Height));

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>
/// 马赛克、模糊的计算结果缓存(MAIO EffectCache):拖动其他对象时不必重新计算。结果是全局像素矩形 + 位图。
/// </summary>
internal sealed class EffectCache : IDisposable
{
    public const int Limit = 48;

    private readonly Dictionary<(int Kind, RectD Rect, double Parameter), Result> _results = [];

    internal sealed record Result(CanvasBitmap Image, RectD Rect);

    /// <summary>
    /// 马赛克:每块取平均色,不保留原始像素,不可还原(MAIO:缩小再用最近邻放大)。
    /// 块数 = 区域像素数 ÷ 块大小 向上取整;每个像素按比例落到某一块,画的时候最近邻放大回去,两边对应关系完全一致。
    /// </summary>
    public Result? Mosaic(ICanvasResourceCreator device, RectD rect, double block, EffectSource source) =>
        Cached((0, rect, block), () =>
        {
            var pixels = source.PixelRect(rect);
            if (pixels.IsEmpty)
            {
                return null;
            }

            var blockPixels = Math.Max(2, block * source.Zoom);
            var columns = Math.Max(1, (int)Math.Ceiling(pixels.Width / blockPixels));
            var rows = Math.Max(1, (int)Math.Ceiling(pixels.Height / blockPixels));
            var small = Average(source.Image, pixels, columns, rows);
            var bitmap = CanvasBitmap.CreateFromBytes(device, small, columns, rows, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            return new Result(bitmap, source.GlobalRect(pixels));
        });

    /// <summary>高斯模糊(MAIO:CoreImage,边缘外延后再模糊,再裁回原区域)。<paramref name="radius"/> 是像素。</summary>
    public Result? Blur(ICanvasResourceCreator device, RectD rect, double radius, EffectSource source) =>
        Cached((1, rect, radius), () =>
        {
            var pixels = source.PixelRect(rect);
            if (pixels.IsEmpty)
            {
                return null;
            }

            var crop = source.Image.Crop(pixels);
            using var input = CanvasBitmap.CreateFromBytes(device, crop.Pixels, crop.Width, crop.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            using var border = new BorderEffect { Source = input, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp };
            using var blur = new GaussianBlurEffect { Source = border, BlurAmount = (float)(radius * source.Zoom), BorderMode = EffectBorderMode.Hard };
            var target = new CanvasRenderTarget(device, crop.Width, crop.Height, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.DrawImage(blur);
            }

            return new Result(target, source.GlobalRect(pixels));
        });

    public void Dispose()
    {
        foreach (var result in _results.Values)
        {
            result.Image.Dispose();
        }

        _results.Clear();
    }

    /// <summary>每块的平均色(BGRA,自上而下)。</summary>
    internal static byte[] Average(PixelBuffer image, PxRect pixels, int columns, int rows)
    {
        var sums = new long[columns * rows * 3];
        var counts = new int[columns * rows];
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = y * rows / pixels.Height;
            var offset = ((pixels.Y + y) * image.Width + pixels.X) * 4;
            for (var x = 0; x < pixels.Width; x++, offset += 4)
            {
                var cell = row * columns + x * columns / pixels.Width;
                sums[cell * 3] += image.Pixels[offset];
                sums[cell * 3 + 1] += image.Pixels[offset + 1];
                sums[cell * 3 + 2] += image.Pixels[offset + 2];
                counts[cell]++;
            }
        }

        var result = new byte[columns * rows * 4];
        for (var cell = 0; cell < counts.Length; cell++)
        {
            var n = Math.Max(1, counts[cell]);
            result[cell * 4] = (byte)(sums[cell * 3] / n);
            result[cell * 4 + 1] = (byte)(sums[cell * 3 + 1] / n);
            result[cell * 4 + 2] = (byte)(sums[cell * 3 + 2] / n);
            result[cell * 4 + 3] = 255;
        }

        return result;
    }

    private Result? Cached((int, RectD, double) key, Func<Result?> make)
    {
        if (_results.TryGetValue(key, out var hit))
        {
            return hit;
        }

        if (make() is not { } result)
        {
            return null;
        }

        if (_results.Count >= Limit)
        {
            Dispose();
        }

        _results[key] = result;
        return result;
    }
}
