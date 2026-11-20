namespace Waio.Feature.Clipboard.Infrastructure;

/// <summary>
/// 剪贴板里的图片是 DIB(BITMAPINFOHEADER + 调色板 + 像素,没有文件头)。
/// 补一个 14 字节的 BMP 文件头它就是合法的 .bmp,WinUI 的 BitmapImage 能直接解码显示(ADR-0009)。
/// </summary>
internal static class DibImage
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const uint BiBitfields = 3;

    /// <summary>转成 BMP 文件字节。不是 BITMAPINFOHEADER 及以上格式的 DIB(或数据残缺)返回 null。</summary>
    public static byte[]? ToBmp(byte[] dib)
    {
        if (dib.Length < InfoHeaderSize)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < InfoHeaderSize || headerSize > dib.Length)
        {
            return null; // 旧式 BITMAPCOREHEADER(12 字节)等:不支持
        }

        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colorsUsed = BitConverter.ToUInt32(dib, 32);

        long paletteBytes = bitCount <= 8
            ? 4L * (colorsUsed != 0 ? colorsUsed : 1u << bitCount)
            : 4L * colorsUsed;
        if (compression == BiBitfields && headerSize == InfoHeaderSize)
        {
            paletteBytes += 12; // 三个颜色掩码跟在 40 字节头后面
        }

        var pixelOffset = FileHeaderSize + headerSize + paletteBytes;
        if (pixelOffset > FileHeaderSize + (long)dib.Length)
        {
            return null;
        }

        var bmp = new byte[FileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes((int)pixelOffset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, FileHeaderSize);
        return bmp;
    }

    /// <summary>图片像素尺寸(高度取绝对值:自上而下的 DIB 高度是负数)。无法解析返回 null。</summary>
    public static (int Width, int Height)? SizeOf(byte[] dib)
    {
        if (dib.Length < InfoHeaderSize || BitConverter.ToInt32(dib, 0) < InfoHeaderSize)
        {
            return null;
        }

        return (BitConverter.ToInt32(dib, 4), Math.Abs(BitConverter.ToInt32(dib, 8)));
    }
}
