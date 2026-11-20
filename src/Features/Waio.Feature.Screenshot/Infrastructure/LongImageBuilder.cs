using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 收集每一帧要用的那段行,最后一次性合成长图(MAIO LongImageBuilder;ADR-0020)。
/// 每段立即复制成独立的小图,不持有整帧,内存只和最终长图差不多;同时保留一份缩小的,用于右侧的长条预览。
/// 拼接位置由 <see cref="StitchPlan"/> 按绝对坐标决定。不是线程安全的:同一时刻只能有一个线程使用。
/// </summary>
internal sealed class LongImageBuilder
{
    private sealed record Part(PixelBuffer Image, PixelBuffer Thumbnail, int Origin);

    private readonly Dictionary<int, Part> _parts = [];
    private int _frameCount = 1;

    /// <param name="previewWidth">预览图的宽度(像素)。</param>
    public LongImageBuilder(PixelBuffer first, int previewWidth = 320)
    {
        Plan = new StitchPlan(first.Height);
        Plan.Begin(0);
        Width = first.Width;
        PreviewScale = Math.Min(1, previewWidth / (double)Math.Max(1, first.Width));
        var copy = first.Crop(new PxRect(0, 0, first.Width, first.Height));
        _parts[0] = new Part(copy, Scale(copy, PreviewScale), 0);
    }

    public StitchPlan Plan { get; }

    public int Width { get; }

    public int Height => Plan.CanvasHeight;

    /// <summary>预览相对原图的缩放。</summary>
    public double PreviewScale { get; }

    /// <summary>接上相对上一帧滚动了 <paramref name="offset"/> 行的一帧(正 = 往下滚)。返回画布有没有变。</summary>
    public bool Append(PixelBuffer frame, int offset)
    {
        if (frame.Width != Width || frame.Height != Plan.FrameHeight)
        {
            return false;
        }

        var index = _frameCount;
        if (Plan.Append(index, offset) is not { } rows)
        {
            return false;
        }

        var strip = frame.Crop(new PxRect(0, rows.Start, frame.Width, rows.End - rows.Start));
        _parts[index] = new Part(strip, Scale(strip, PreviewScale), rows.Start);
        _frameCount++;

        // 被完全覆盖的段不再需要
        var used = Plan.UsedFrames;
        foreach (var key in _parts.Keys.Where(k => !used.Contains(k)).ToList())
        {
            _parts.Remove(key);
        }

        return true;
    }

    /// <summary>合成长图。</summary>
    public PixelBuffer Compose()
    {
        var output = new byte[Width * Height * 4];
        var stride = Width * 4;
        foreach (var strip in Plan.Strips)
        {
            if (_parts.TryGetValue(strip.Frame, out var part))
            {
                Buffer.BlockCopy(part.Image.Pixels, (strip.Start - part.Origin) * stride, output, strip.Top * stride, strip.Height * stride);
            }
        }

        PixelBuffer.MakeOpaque(output);
        return new PixelBuffer(Width, Height, output);
    }

    /// <summary>合成缩小的预览(由每段的缩略图拼成,代价很小)。</summary>
    public PixelBuffer Preview()
    {
        var width = Math.Max(1, Round(Width * PreviewScale));
        var height = Math.Max(1, Round(Height * PreviewScale));
        var output = new byte[width * height * 4];
        var stride = width * 4;
        foreach (var strip in Plan.Strips)
        {
            if (!_parts.TryGetValue(strip.Frame, out var part))
            {
                continue;
            }

            // 目标行按画布坐标分别取整(相邻两段首尾相接,不会因为取整留下黑缝),源行对应过去、不够时重复最后一行
            var thumbnail = part.Thumbnail;
            if (thumbnail.Width != width)
            {
                continue;
            }

            var from = Round((strip.Start - part.Origin) * PreviewScale);
            var top = Round(strip.Top * PreviewScale);
            var bottom = Math.Min(height, Round((strip.Top + strip.Height) * PreviewScale));
            for (var y = top; y < bottom; y++)
            {
                var source = Math.Clamp(from + y - top, 0, thumbnail.Height - 1);
                Buffer.BlockCopy(thumbnail.Pixels, source * stride, output, y * stride, stride);
            }
        }

        PixelBuffer.MakeOpaque(output);
        return new PixelBuffer(width, height, output);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>块平均缩小(<paramref name="factor"/> ≤ 1)。</summary>
    internal static PixelBuffer Scale(PixelBuffer image, double factor)
    {
        if (factor >= 1)
        {
            return image;
        }

        var width = Math.Max(1, Round(image.Width * factor));
        var height = Math.Max(1, Round(image.Height * factor));
        return Resize(image, width, height);
    }

    /// <summary>块平均缩放到 <paramref name="width"/> × <paramref name="height"/>(只缩小;放大时退化为最近邻)。</summary>
    internal static PixelBuffer Resize(PixelBuffer image, int width, int height)
    {
        var source = image.Pixels;
        var output = new byte[width * height * 4];
        for (var ty = 0; ty < height; ty++)
        {
            var y0 = ty * image.Height / height;
            var y1 = Math.Max(y0 + 1, (ty + 1) * image.Height / height);
            for (var tx = 0; tx < width; tx++)
            {
                var x0 = tx * image.Width / width;
                var x1 = Math.Max(x0 + 1, (tx + 1) * image.Width / width);
                int b = 0, g = 0, r = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var i = (y * image.Width + x) * 4;
                        b += source[i];
                        g += source[i + 1];
                        r += source[i + 2];
                    }
                }

                var count = (y1 - y0) * (x1 - x0);
                var o = (ty * width + tx) * 4;
                output[o] = (byte)(b / count);
                output[o + 1] = (byte)(g / count);
                output[o + 2] = (byte)(r / count);
                output[o + 3] = 255;
            }
        }

        return new PixelBuffer(width, height, output);
    }
}
