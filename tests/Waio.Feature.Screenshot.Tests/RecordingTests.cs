using System.Runtime.InteropServices.WindowsRuntime;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Recording;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Recording;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>录屏的尺寸、码率、文件名(MAIO RecordingGeometryTests)。</summary>
public class RecordingGeometryTests
{
    [Fact]
    public void 奇数宽高裁成偶数_不缩放()
    {
        Assert.Equal((800, 600, 800, 600), RecordingGeometry.Mp4(801, 601));
    }

    [Fact]
    public void 超过H264硬件上限时等比缩小()
    {
        var (cw, ch, ow, oh) = RecordingGeometry.Mp4(5120, 2880);
        Assert.Equal((5120, 2880), (cw, ch));
        Assert.True(ow * oh <= RecordingGeometry.MaxH264Pixels);
        Assert.True(ow <= 4096 && ow % 2 == 0 && oh % 2 == 0);
        Assert.Equal(16 / 9.0, ow / (double)oh, 2);
    }

    [Fact]
    public void 码率按像素和帧率算并限制范围()
    {
        Assert.Equal((int)(1920 * 1080 * 30 * 0.12), RecordingGeometry.Bitrate(1920, 1080, 30));
        Assert.Equal(RecordingGeometry.MinBitrate, RecordingGeometry.Bitrate(100, 100, 10));
        Assert.Equal(RecordingGeometry.MaxBitrate, RecordingGeometry.Bitrate(4096, 2304, 60));
    }

    [Fact]
    public void Gif默认按逻辑像素_高清按物理像素()
    {
        Assert.Equal((800, 400), RecordingGeometry.Gif(1200, 600, 1.5, highQuality: false));
        Assert.Equal((1200, 600), RecordingGeometry.Gif(1200, 600, 1.5, highQuality: true));
        Assert.Equal((1200, 600), RecordingGeometry.Gif(1200, 600, 1, highQuality: false));
    }

    [Fact]
    public void 时长和文件名()
    {
        Assert.Equal("01:05", RecordingGeometry.Elapsed(TimeSpan.FromSeconds(65)));
        Assert.Equal("1:01:05", RecordingGeometry.Elapsed(TimeSpan.FromSeconds(3665)));
        var date = new DateTimeOffset(2026, 11, 11, 14, 5, 33, TimeSpan.FromHours(8));
        Assert.Equal("WAIO 录屏 2026-11-11 14.05.33.gif", RecordingGeometry.FileName(date, RecordingFormat.Gif));
    }
}

/// <summary>混音(MAIO AudioMixerTests)。</summary>
public class AudioMixerTests
{
    private static float[] Tone(int frames, float value) => Enumerable.Repeat(value, frames * 2).ToArray();

    [Fact]
    public void 两路都有数据的部分立即混出并相加()
    {
        var mixer = new AudioMixer(2);
        mixer.Push(0, 0, Tone(100, 0.25f));
        mixer.Push(1, 0, Tone(60, 0.5f));
        var mixed = mixer.Mix(nowFrame: 100);
        Assert.Equal(60 * 2, mixed.Length);
        Assert.All(mixed, v => Assert.Equal(0.75f, v));
    }

    [Fact]
    public void 某一路没有数据时最多等零点二秒_之后当作静音()
    {
        var mixer = new AudioMixer(2);
        mixer.Push(0, 0, Tone(20_000, 0.25f));
        Assert.Empty(mixer.Mix(nowFrame: 5_000)); // 还在等第二路
        var mixed = mixer.Mix(nowFrame: 20_000 + AudioMixer.MaxWaitFrames);
        Assert.Equal(20_000 * 2, mixed.Length);
        Assert.All(mixed, v => Assert.Equal(0.25f, v));
    }

    [Fact]
    public void 小间隔当作连续_大缺口补静音_重叠丢掉()
    {
        var mixer = new AudioMixer(1);
        mixer.Push(0, 0, Tone(100, 0.1f));
        mixer.Push(0, 150, Tone(100, 0.2f)); // 缺口 50 帧(约 1 ms):接在 100 后面
        mixer.Push(0, 1_000, Tone(100, 0.3f)); // 缺口 800 帧:补静音
        mixer.Push(0, 1_050, Tone(100, 0.4f)); // 和上一段重叠 50 帧:只留后 50 帧
        var mixed = mixer.Flush(1_150);
        Assert.Equal(1_150 * 2, mixed.Length);
        Assert.Equal(0.1f, mixed[99 * 2]);
        Assert.Equal(0.2f, mixed[100 * 2]);
        Assert.Equal(0f, mixed[500 * 2]);
        Assert.Equal(0.3f, mixed[1_000 * 2]);
        Assert.Equal(0.4f, mixed[1_120 * 2]);
    }

    [Fact]
    public void 相加后限制在正负一之间_录制前的数据丢掉()
    {
        var mixer = new AudioMixer(2);
        mixer.Push(0, -50, Tone(150, 0.8f));
        mixer.Push(1, 0, Tone(100, 0.8f));
        var mixed = mixer.Mix(100);
        Assert.Equal(200, mixed.Length);
        Assert.All(mixed, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void 格式转换()
    {
        Assert.Equal([0.5f, 0.5f, -1f, -1f], AudioConvert.ToStereo([0.5f, -1f], 1));
        Assert.Equal([1f, 2f, 5f, 6f], AudioConvert.ToStereo([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], 4));
        Assert.Equal([0.5f, -1f], AudioConvert.FromInt16([0x00, 0x40, 0x00, 0x80]));
        Assert.Equal(480 * 2, AudioConvert.Resample(new float[441 * 2], 44_100, 48_000).Length);
        Assert.Equal([0xFF, 0x7F, 0x01, 0x80], AudioConvert.ToInt16([1f, -1f]));
    }
}

/// <summary>GIF 编码:用系统的 GIF 解码器(WIC)读回来逐像素核对(MAIO 用 ImageIO 核对)。</summary>
public class GifEncoderTests
{
    private static byte[] Frame(int width, int height, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = color(x, y);
                var i = (y * width + x) * 4;
                (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) = (b, g, r, 255);
            }
        }

        return bytes;
    }

    private static async Task<(BitmapDecoder Decoder, List<(byte[] Pixels, uint X, uint Y, uint W, uint H, ushort Delay)> Frames)> DecodeAsync(byte[] gif)
    {
        var stream = new MemoryStream(gif).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);
        var frames = new List<(byte[], uint, uint, uint, uint, ushort)>();
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var props = await frame.BitmapProperties.GetPropertiesAsync(
                ["/imgdesc/Left", "/imgdesc/Top", "/imgdesc/Width", "/imgdesc/Height", "/grctlext/Delay"]);
            var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            frames.Add((data.DetachPixelData(), (ushort)props["/imgdesc/Left"].Value, (ushort)props["/imgdesc/Top"].Value,
                (ushort)props["/imgdesc/Width"].Value, (ushort)props["/imgdesc/Height"].Value, (ushort)props["/grctlext/Delay"].Value));
        }

        return (decoder, frames);
    }

    [Fact]
    public async Task 颜色少时无损_只编码变化的矩形_其余透明()
    {
        // 第一帧:4 种颜色的格子;第二帧:只有 (10,5)-(14,8) 变红
        static (byte, byte, byte) Grid(int x, int y) => ((byte)(x / 8 % 2 * 200), (byte)(y / 8 % 2 * 150), 40);
        var first = Frame(40, 30, Grid);
        var second = Frame(40, 30, (x, y) => x is >= 10 and < 15 && y is >= 5 and < 9 ? ((byte)255, (byte)0, (byte)0) : Grid(x, y));
        using var output = new MemoryStream();
        using (var encoder = new GifEncoder(output, 40, 30))
        {
            encoder.AddFrame(first, TimeSpan.Zero);
            encoder.AddFrame(first, TimeSpan.FromMilliseconds(40)); // 完全相同:不算一帧
            encoder.AddFrame(second, TimeSpan.FromMilliseconds(100));
            encoder.Finish(TimeSpan.FromMilliseconds(250));
            Assert.Equal(2, encoder.FrameCount);
        }

        var (decoder, frames) = await DecodeAsync(output.ToArray());
        Assert.Equal((40u, 30u), (decoder.PixelWidth, decoder.PixelHeight));
        Assert.Equal(2, frames.Count);
        Assert.Equal(first, frames[0].Pixels); // 无损
        Assert.Equal((ushort)10, frames[0].Delay); // 0 → 100 ms
        Assert.Equal((10u, 5u, 5u, 4u), (frames[1].X, frames[1].Y, frames[1].W, frames[1].H));
        Assert.Equal((ushort)15, frames[1].Delay); // 100 → 250 ms
        Assert.All(Enumerable.Range(0, 20), i => Assert.Equal((byte)255, frames[1].Pixels[i * 4 + 2])); // 变化的像素都是红色、不透明
    }

    [Fact]
    public async Task 颜色多时中位切分_大图跨过LZW码表重置()
    {
        // 300×200 随机颜色:远多于 256 种,LZW 码表会满好几次
        var random = new Random(3);
        var bytes = Frame(300, 200, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using var output = new MemoryStream();
        using (var encoder = new GifEncoder(output, 300, 200))
        {
            encoder.AddFrame(bytes, TimeSpan.Zero);
            encoder.Finish(TimeSpan.FromMilliseconds(500));
        }

        var (_, frames) = await DecodeAsync(output.ToArray());
        var decoded = frames[0].Pixels;
        Assert.Equal(bytes.Length, decoded.Length);
        double error = 0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            error += Math.Abs(bytes[i] - decoded[i]) + Math.Abs(bytes[i + 1] - decoded[i + 1]) + Math.Abs(bytes[i + 2] - decoded[i + 2]);
        }

        Assert.True(error / (bytes.Length / 4 * 3) < 40, $"平均误差 {error / (bytes.Length / 4 * 3):F1}"); // 随机噪声 255 色,误差有限
    }

    [Fact]
    public async Task 帧时长按绝对时间取整_不累积误差_最短两百分之一秒()
    {
        using var output = new MemoryStream();
        using (var encoder = new GifEncoder(output, 4, 4))
        {
            for (var i = 0; i < 6; i++)
            {
                encoder.AddFrame(Frame(4, 4, (_, _) => ((byte)(i * 40), 0, 0)), TimeSpan.FromMilliseconds(i * 66.7));
            }

            encoder.AddFrame(Frame(4, 4, (_, _) => (1, 2, 3)), TimeSpan.FromMilliseconds(400.5)); // 和上一帧只差 1/1000 秒以内
            encoder.Finish(TimeSpan.FromMilliseconds(420));
        }

        var (_, frames) = await DecodeAsync(output.ToArray());
        var delays = frames.Select(f => (int)f.Delay).ToList();
        Assert.Equal(42, delays.Sum()); // 0 → 420 ms(最后一帧 400.5 → 420 只有 2/100 秒)
        Assert.All(delays, d => Assert.True(d >= GifEncoder.MinDelay));
        Assert.Equal([7, 6, 7, 7, 6, 7], delays.Take(6)); // 0, 6.67, 13.34 … 取整后的差
    }
}

/// <summary>MP4:真的用 Media Foundation 编一段(只写临时文件,不碰屏幕),再用系统读回来核对。</summary>
public sealed class Mp4WriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-recording-tests", Guid.NewGuid().ToString("N"));

    public Mp4WriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>上半红、下半蓝(用来确认画面没有上下颠倒)。</summary>
    private static byte[] RedOverBlue(int width, int height)
    {
        var image = PixelBuffer.Solid(width, height, new SampledColor(220, 20, 20));
        for (var y = height / 2; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image.SetColor(x, y, new SampledColor(20, 20, 220));
            }
        }

        return image.Pixels;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task 编出的MP4尺寸时长正确_画面没有颠倒(bool hardware, bool audio)
    {
        var path = Path.Combine(_dir, $"test-{hardware}-{audio}.mp4");
        var frame = RedOverBlue(320, 240);
        using (var writer = Mp4Writer.Create(path, 320, 240, 30, audio, preferHardware: hardware))
        {
            for (var i = 0; i < 60; i++)
            {
                writer.WriteVideo(frame, TimeSpan.FromSeconds(i / 30.0), TimeSpan.FromSeconds(1 / 30.0));
            }

            if (audio)
            {
                var tone = new float[AudioMixer.SampleRate / 10 * 2];
                for (var i = 0; i < tone.Length / 2; i++)
                {
                    tone[i * 2] = tone[i * 2 + 1] = 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / AudioMixer.SampleRate);
                }

                for (var i = 0; i < 20; i++)
                {
                    writer.WriteAudio(tone, TimeSpan.FromSeconds(i / 10.0));
                }
            }

            writer.Finish();
        }

        Assert.True(new FileInfo(path).Length > 1000);
        var file = await StorageFile.GetFileFromPathAsync(path);
        var properties = await file.Properties.GetVideoPropertiesAsync();
        Assert.Equal((320u, 240u), (properties.Width, properties.Height));
        Assert.InRange(properties.Duration.TotalSeconds, 1.8, 2.2);

        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));
        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(1), 320, 240, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var width = (int)decoder.PixelWidth;
        var top = (60 * width + width / 2) * 4;
        var bottom = ((int)(decoder.PixelHeight * 3 / 4) * width + width / 2) * 4;
        Assert.True(pixels[top + 2] > 150 && pixels[top] < 100, $"上半应为红色:R={pixels[top + 2]} B={pixels[top]}");
        Assert.True(pixels[bottom] > 150 && pixels[bottom + 2] < 100, $"下半应为蓝色:R={pixels[bottom + 2]} B={pixels[bottom]}");
    }

    [Fact]
    public void 奇数宽高直接拒绝()
    {
        Assert.Throws<ArgumentException>(() => Mp4Writer.Create(Path.Combine(_dir, "odd.mp4"), 321, 240, 30, audio: false));
    }
}

/// <summary>只在设置环境变量 WAIO_SCREEN_READ=1 时运行:会读取真实屏幕内容和系统声音(不开窗口、不发按键,文件用完即删)。</summary>
public sealed class ScreenReadFactAttribute : FactAttribute
{
    public ScreenReadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WAIO_SCREEN_READ") != "1")
        {
            Skip = "设置环境变量 WAIO_SCREEN_READ=1 才运行(会读取真实屏幕和系统声音)";
        }
    }
}

/// <summary>整条录制链路:BitBlt 取帧 + 系统声音回环 + Media Foundation / GIF 写文件。</summary>
public sealed class ScreenRecorderIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-recorder-tests", Guid.NewGuid().ToString("N"));

    public ScreenRecorderIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [ScreenReadFact]
    public async Task 录一秒半MP4_带系统声音()
    {
        var path = Path.Combine(_dir, "screen.mp4");
        var options = new ScreenRecorder.Options(new PxRect(0, 0, 640, 360), (640, 360), RecordingFormat.Mp4, 30, SystemAudio: true, Microphone: false, Cursor: true, path);
        var recorder = ScreenRecorder.Start(options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await Task.Delay(1500);
        var elapsed = await recorder.StopAsync(keep: true);
        Assert.Empty(recorder.Warnings);
        var properties = await (await StorageFile.GetFileFromPathAsync(path)).Properties.GetVideoPropertiesAsync();
        Assert.Equal((640u, 360u), (properties.Width, properties.Height));
        Assert.InRange(properties.Duration.TotalSeconds, elapsed.TotalSeconds - 0.3, elapsed.TotalSeconds + 0.3);
    }

    [ScreenReadFact]
    public async Task 录一秒GIF()
    {
        var path = Path.Combine(_dir, "screen.gif");
        var options = new ScreenRecorder.Options(new PxRect(0, 0, 400, 300), (267, 200), RecordingFormat.Gif, 15, false, false, true, path);
        var recorder = ScreenRecorder.Start(options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await Task.Delay(1000);
        await recorder.StopAsync(keep: true);
        using var file = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, file.AsRandomAccessStream());
        Assert.Equal((267u, 200u), (decoder.PixelWidth, decoder.PixelHeight));
        Assert.True(decoder.FrameCount >= 1);
    }
}
