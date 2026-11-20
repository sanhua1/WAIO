namespace Waio.Feature.Screenshot.Domain.Recording;

/// <summary>录屏格式(MAIO RecordingFormat)。</summary>
public enum RecordingFormat
{
    Mp4,
    Gif,
}

/// <summary>
/// 录屏的尺寸与码率(MAIO RecordingGeometry,ADR-0021)。
/// <list type="bullet">
/// <item>H.264 要求宽高是偶数:选区是奇数时裁掉最右 / 最下一像素(不缩放,画面不糊);</item>
/// <item>H.264 硬件编码一帧最多 4096×2304 个像素(Level 5.1):超过时等比缩小;</item>
/// <item>码率 = 宽 × 高 × 帧率 × 0.12 比特,限制在 2~60 Mbps(MAIO 同)。</item>
/// </list>
/// </summary>
public static class RecordingGeometry
{
    public const int MaxH264Pixels = 4096 * 2304;
    public const int MaxH264Side = 4096;
    public const double BitsPerPixel = 0.12;
    public const int MinBitrate = 2_000_000;
    public const int MaxBitrate = 60_000_000;

    /// <summary>选区 <paramref name="width"/> × <paramref name="height"/> 录成 MP4 时实际截取的大小和输出大小(都是偶数)。</summary>
    public static (int CaptureWidth, int CaptureHeight, int OutputWidth, int OutputHeight) Mp4(int width, int height)
    {
        var captureWidth = Math.Max(2, width & ~1);
        var captureHeight = Math.Max(2, height & ~1);
        var scale = Math.Min(1.0, Math.Min(
            Math.Sqrt(MaxH264Pixels / ((double)captureWidth * captureHeight)),
            MaxH264Side / (double)Math.Max(captureWidth, captureHeight)));
        if (scale >= 1)
        {
            return (captureWidth, captureHeight, captureWidth, captureHeight);
        }

        var outputWidth = Math.Max(2, (int)(captureWidth * scale) & ~1);
        var outputHeight = Math.Max(2, (int)(captureHeight * scale) & ~1);
        return (captureWidth, captureHeight, outputWidth, outputHeight);
    }

    public static int Bitrate(int width, int height, int fps) =>
        (int)Math.Clamp(width * (double)height * fps * BitsPerPixel, MinBitrate, MaxBitrate);

    /// <summary>
    /// GIF 的输出大小:默认按逻辑像素录(150% 缩放时宽高乘 1/1.5,文件小得多;MAIO 默认按 pt 录),
    /// <paramref name="highQuality"/> 时按物理像素。
    /// </summary>
    public static (int Width, int Height) Gif(int width, int height, double scale, bool highQuality)
    {
        if (highQuality || scale <= 1)
        {
            return (width, height);
        }

        return (Math.Max(1, (int)Math.Round(width / scale)), Math.Max(1, (int)Math.Round(height / scale)));
    }

    /// <summary>录了多久,显示成 "01:05"(超过一小时 "1:01:05")。</summary>
    public static string Elapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    /// <summary>录屏文件名:"WAIO 录屏 2026-11-11 14.05.33.mp4"(和截图的命名方式一致)。</summary>
    public static string FileName(DateTimeOffset date, RecordingFormat format) =>
        $"WAIO 录屏 {date:yyyy-MM-dd HH.mm.ss}.{(format == RecordingFormat.Gif ? "gif" : "mp4")}";
}
