using System.Buffers.Binary;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>PNG 编码 / 解码、缩略图、剪贴板用的 DIB(MAIO ImageExporter 的编码部分)。用系统自带的 WIC(Windows.Graphics.Imaging),不引入第三方库。</summary>
internal static class ImageCodec
{
    /// <summary>编码成 PNG。<paramref name="scale"/> 写进 PNG 的 DPI(150% 写 144),Office 等按它决定插入后的大小。</summary>
    /// <param name="maxSide">给了就等比缩小到最长边不超过它(缩略图)。</param>
    public static async Task<byte[]> EncodePngAsync(PixelBuffer image, double scale = 1, int? maxSide = null)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = 96 * Math.Max(scale, 0.1);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height, dpi, dpi, image.Pixels);
        if (maxSide is { } side && Math.Max(image.Width, image.Height) > side)
        {
            var ratio = (double)side / Math.Max(image.Width, image.Height);
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(image.Width * ratio));
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(image.Height * ratio));
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();
        return await ReadAllAsync(stream);
    }

    /// <summary>
    /// 解码图片(PNG、JPEG、BMP 等 WIC 支持的格式;重新贴图、编辑历史截图、贴剪贴板图片时用)。透明的地方叠到白底上。
    /// </summary>
    public static async Task<PixelBuffer> DecodeAsync(byte[] data)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(data);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var bytes = pixels.DetachPixelData();
        PixelBuffer.FlattenOnWhite(bytes);
        return new PixelBuffer((int)decoder.PixelWidth, (int)decoder.PixelHeight, bytes);
    }

    /// <summary>
    /// 剪贴板用的 CF_DIB:BITMAPINFOHEADER + 自下而上的 32 位像素。
    /// MAIO 只写 PNG;Windows 上图片的标准格式是 DIB,微信、Office、画图等都认它。
    /// </summary>
    public static byte[] ToDib(PixelBuffer image, double scale = 1)
    {
        const int headerSize = 40;
        var data = new byte[headerSize + image.Pixels.Length];
        var span = data.AsSpan();
        var pixelsPerMeter = (int)Math.Round(96 * Math.Max(scale, 0.1) / 0.0254);
        BinaryPrimitives.WriteInt32LittleEndian(span, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], image.Height); // 正数 = 自下而上(兼容性最好)
        BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 0); // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], image.Pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], pixelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], pixelsPerMeter);
        for (var row = 0; row < image.Height; row++)
        {
            System.Buffer.BlockCopy(image.Pixels, row * image.Stride, data, headerSize + (image.Height - 1 - row) * image.Stride, image.Stride);
        }

        return data;
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
