using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 一张图的像素:BGRA 各 8 位、从上到下逐行、不透明(Alpha 恒为 255)。截到的屏幕、裁剪后的截图都用它。
/// 对应 MAIO 里的 CGImage;冻结的整屏画面每屏一张,截图结束后立即释放(4K 约 33 MB)。
/// </summary>
internal sealed class PixelBuffer
{
    public PixelBuffer(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"像素数据和尺寸不符:{width}×{height},{pixels.Length} 字节");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    /// <summary>纯色图(测试用)。</summary>
    public static PixelBuffer Solid(int width, int height, SampledColor color)
    {
        var buffer = new PixelBuffer(width, height, new byte[width * height * 4]);
        MemoryMarshal.Cast<byte, uint>(buffer.Pixels.AsSpan()).Fill(Pack(color));
        return buffer;
    }

    public static uint Pack(SampledColor c) => 0xFF000000u | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;

    /// <summary>把 Alpha 全部设为 255。GDI 截屏得到的 Alpha 是 0,不改的话 PNG / WinUI 会当成全透明。</summary>
    public static void MakeOpaque(Span<byte> bgra)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] |= 0xFF000000u;
        }
    }

    /// <summary>预乘 Alpha 的 BGRA 叠到白底上并设为不透明(剪贴板里带透明的图片,透明处变白而不是黑)。</summary>
    public static void FlattenOnWhite(Span<byte> premultiplied)
    {
        for (var i = 0; i < premultiplied.Length; i += 4)
        {
            var transparent = 255 - premultiplied[i + 3];
            if (transparent != 0)
            {
                premultiplied[i] = (byte)Math.Min(255, premultiplied[i] + transparent);
                premultiplied[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] + transparent);
                premultiplied[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] + transparent);
                premultiplied[i + 3] = 255;
            }
        }
    }

    /// <summary>顺时针旋转 90° 的 <paramref name="quarterTurns"/> 倍(贴图旋转后复制、保存,MAIO ImageRotation)。</summary>
    public PixelBuffer Rotated(int quarterTurns)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0)
        {
            return this;
        }

        var (width, height) = turns % 2 == 1 ? (Height, Width) : (Width, Height);
        var source = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());
        var result = new byte[width * height * 4];
        var target = MemoryMarshal.Cast<byte, uint>(result.AsSpan());
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (tx, ty) = turns switch
                {
                    1 => (Height - 1 - y, x),
                    2 => (Width - 1 - x, Height - 1 - y),
                    _ => (y, Width - 1 - x),
                };
                target[ty * width + tx] = source[y * Width + x];
            }
        }

        return new PixelBuffer(width, height, result);
    }

    /// <summary>某个像素的颜色(左上角为原点);越界返回 null(MAIO PixelSampler)。</summary>
    public SampledColor? ColorAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return null;
        }

        var i = (y * Width + x) * 4;
        return new SampledColor(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    public void SetColor(int x, int y, SampledColor color) =>
        MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan())[y * Width + x] = Pack(color);

    /// <summary>裁出一块(<paramref name="rect"/> 以本图左上角为原点,超出部分被截掉)。</summary>
    public PixelBuffer Crop(PxRect rect)
    {
        var clipped = rect.Intersect(new PxRect(0, 0, Width, Height));
        if (clipped.IsEmpty)
        {
            throw new ArgumentException($"裁剪区域 {rect} 不在图片 {Width}×{Height} 里");
        }

        var result = new byte[clipped.Width * clipped.Height * 4];
        var rowBytes = clipped.Width * 4;
        for (var row = 0; row < clipped.Height; row++)
        {
            Buffer.BlockCopy(Pixels, ((clipped.Y + row) * Width + clipped.X) * 4, result, row * rowBytes, rowBytes);
        }

        return new PixelBuffer(clipped.Width, clipped.Height, result);
    }
}
