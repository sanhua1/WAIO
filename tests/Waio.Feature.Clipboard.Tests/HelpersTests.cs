using Waio.Feature.Clipboard.Domain;
using Waio.Feature.Clipboard.Infrastructure;

namespace Waio.Feature.Clipboard.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(59, "刚刚")]
    [InlineData(60, "1 分钟前")]
    [InlineData(59 * 60, "59 分钟前")]
    [InlineData(3600, "1 小时前")]
    [InlineData(23 * 3600, "23 小时前")]
    [InlineData(24 * 3600, "昨天")]
    [InlineData(47 * 3600, "昨天")]
    public void 相对时间文案(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void 超过两天_显示日期()
    {
        var text = RelativeTime.Format(Now.AddDays(-5), Now);

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", text);
    }

    [Fact]
    public void 时钟回拨导致未来时间_显示刚刚()
    {
        Assert.Equal("刚刚", RelativeTime.Format(Now.AddMinutes(5), Now));
    }
}

public class DibImageTests
{
    /// <summary>构造一个最小的 DIB:BITMAPINFOHEADER + 像素。</summary>
    private static byte[] MakeDib(int width, int height, ushort bitCount, uint compression = 0, uint colorsUsed = 0, int extraBeforePixels = 0, int pixelBytes = 16)
    {
        var header = new byte[40];
        BitConverter.GetBytes(40).CopyTo(header, 0);
        BitConverter.GetBytes(width).CopyTo(header, 4);
        BitConverter.GetBytes(height).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 12);
        BitConverter.GetBytes(bitCount).CopyTo(header, 14);
        BitConverter.GetBytes(compression).CopyTo(header, 16);
        BitConverter.GetBytes(colorsUsed).CopyTo(header, 32);
        return [.. header, .. new byte[extraBeforePixels], .. new byte[pixelBytes]];
    }

    [Fact]
    public void 三十二位图_加14字节文件头_像素偏移是54()
    {
        var dib = MakeDib(2, 2, 32);

        var bmp = DibImage.ToBmp(dib)!;

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));      // 文件总长
        Assert.Equal(14 + 40, BitConverter.ToInt32(bmp, 10));        // 像素数据偏移
        Assert.Equal(dib, bmp[14..]);                                // 原 DIB 原样跟在后面
    }

    [Fact]
    public void 八位图_偏移包含256色调色板()
    {
        var dib = MakeDib(2, 2, 8, extraBeforePixels: 256 * 4);

        var bmp = DibImage.ToBmp(dib)!;

        Assert.Equal(14 + 40 + 1024, BitConverter.ToInt32(bmp, 10));
    }

    [Fact]
    public void 八位图_指定了颜色数_按指定数算()
    {
        var dib = MakeDib(2, 2, 8, colorsUsed: 16, extraBeforePixels: 16 * 4);

        Assert.Equal(14 + 40 + 64, BitConverter.ToInt32(DibImage.ToBmp(dib)!, 10));
    }

    [Fact]
    public void BITFIELDS压缩_偏移多出12字节掩码()
    {
        var dib = MakeDib(2, 2, 32, compression: 3, extraBeforePixels: 12);

        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(DibImage.ToBmp(dib)!, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(39)]
    public void 数据太短_返回null(int length)
    {
        Assert.Null(DibImage.ToBmp(new byte[length]));
        Assert.Null(DibImage.SizeOf(new byte[length]));
    }

    [Fact]
    public void 头部大小不合法_返回null()
    {
        var dib = new byte[100];
        BitConverter.GetBytes(12).CopyTo(dib, 0); // 旧式 BITMAPCOREHEADER

        Assert.Null(DibImage.ToBmp(dib));
    }

    [Fact]
    public void 调色板声称的大小超出数据_返回null_不越界()
    {
        var dib = MakeDib(2, 2, 8, pixelBytes: 0); // 声称有 256 色调色板,实际一个字节都没有

        Assert.Null(DibImage.ToBmp(dib));
    }

    [Fact]
    public void 尺寸_自上而下的DIB高度为负_取绝对值()
    {
        Assert.Equal((640, 480), DibImage.SizeOf(MakeDib(640, -480, 32)));
        Assert.Equal((640, 480), DibImage.SizeOf(MakeDib(640, 480, 32)));
    }
}
