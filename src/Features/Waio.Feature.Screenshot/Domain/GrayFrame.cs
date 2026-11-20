namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 灰度帧(逐行存放,每像素 1 字节),长截图的位移估计只用灰度(MAIO GrayFrame)。
/// 和平台无关,便于用合成数据测试。
/// </summary>
public sealed class GrayFrame
{
    public GrayFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height)
        {
            throw new ArgumentException($"灰度数据和尺寸不符:{width}×{height},{pixels.Length} 字节");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public byte this[int x, int y] => Pixels[y * Width + x];

    /// <summary>
    /// BGRA(从上到下逐行,stride = 宽 × 4)转灰度,同时把宽度抽样到不超过 <paramref name="maxWidth"/>(高度不变,保证 1 像素的竖直精度)。
    /// 灰度用整数近似 (77R + 150G + 29B) >> 8。
    /// </summary>
    public static GrayFrame FromBgra(ReadOnlySpan<byte> bgra, int width, int height, int maxWidth = int.MaxValue)
    {
        var target = Math.Clamp(maxWidth, 1, width);
        var columns = new int[target];
        for (var x = 0; x < target; x++)
        {
            columns[x] = x * width / target * 4;
        }

        var output = new byte[target * height];
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * width * 4, width * 4);
            var line = output.AsSpan(y * target, target);
            for (var x = 0; x < target; x++)
            {
                var i = columns[x];
                line[x] = (byte)((row[i] * 29 + row[i + 1] * 150 + row[i + 2] * 77) >> 8);
            }
        }

        return new GrayFrame(target, height, output);
    }

    /// <summary>只按列抽样(高度不变)。竖直位移不依赖每一列,抽样比块平均便宜得多(MAIO sampled)。</summary>
    public GrayFrame Sampled(int targetWidth)
    {
        targetWidth = Math.Clamp(targetWidth, 1, Width);
        if (targetWidth == Width)
        {
            return this;
        }

        var output = new byte[targetWidth * Height];
        for (var y = 0; y < Height; y++)
        {
            var source = Pixels.AsSpan(y * Width, Width);
            var line = output.AsSpan(y * targetWidth, targetWidth);
            for (var x = 0; x < targetWidth; x++)
            {
                line[x] = source[x * Width / targetWidth];
            }
        }

        return new GrayFrame(targetWidth, Height, output);
    }

    /// <summary>块平均缩小(最近邻会让细线忽隐忽现,块平均更稳;MAIO reduced)。</summary>
    public GrayFrame Reduced(int targetWidth, int targetHeight)
    {
        targetWidth = Math.Clamp(targetWidth, 1, Width);
        targetHeight = Math.Clamp(targetHeight, 1, Height);
        if (targetWidth == Width && targetHeight == Height)
        {
            return this;
        }

        var xs = new int[targetWidth + 1];
        for (var i = 0; i <= targetWidth; i++)
        {
            xs[i] = i * Width / targetWidth;
        }

        var output = new byte[targetWidth * targetHeight];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = ty * Height / targetHeight;
            var y1 = Math.Max(y0 + 1, (ty + 1) * Height / targetHeight);
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = xs[tx];
                var x1 = Math.Max(x0 + 1, xs[tx + 1]);
                var sum = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = Pixels.AsSpan(y * Width + x0, x1 - x0);
                    foreach (var value in row)
                    {
                        sum += value;
                    }
                }

                output[ty * targetWidth + tx] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }

        return new GrayFrame(targetWidth, targetHeight, output);
    }
}
