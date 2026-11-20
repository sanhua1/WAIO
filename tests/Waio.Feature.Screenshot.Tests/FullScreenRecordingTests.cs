using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Recording;
using Waio.Feature.Screenshot.Infrastructure.Recording;
using Windows.Storage;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>全屏录屏的音轨安排(ADR-0030)。</summary>
public class RecordingTracksTests
{
    [Fact]
    public void 区域录屏_两路混成一条()
    {
        Assert.Equal([AudioTrackKind.Mixed], RecordingTracks.Layout(true, true, separate: false));
        Assert.Equal([AudioTrackKind.Microphone], RecordingTracks.Layout(false, true, separate: false));
        Assert.Empty(RecordingTracks.Layout(false, false, separate: false));
    }

    [Fact]
    public void 全屏录屏_各录一条再加一条混音()
    {
        Assert.Equal(
            [AudioTrackKind.System, AudioTrackKind.Microphone, AudioTrackKind.Mixed],
            RecordingTracks.Layout(true, true, separate: true));
        Assert.Equal([AudioTrackKind.System], RecordingTracks.Layout(true, false, separate: true));
    }

    [Theory]
    [InlineData(true, true, 2)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, null)]
    public void 保留哪条_两路都留就是混音(bool system, bool microphone, int? expected)
    {
        var layout = RecordingTracks.Layout(true, true, separate: true);

        Assert.Equal(expected, RecordingTracks.Keep(layout, system, microphone));
    }

    [Fact]
    public void 保留哪条_只录到一路时_要两路也只能给这一路_要没录的给不出()
    {
        var layout = RecordingTracks.Layout(true, false, separate: true);

        Assert.Equal(0, RecordingTracks.Keep(layout, true, true));
        Assert.Null(RecordingTracks.Keep(layout, false, true));
    }

    [Fact]
    public void 峰值()
    {
        Assert.Equal(0.5f, RecordingTracks.Peak([0.1f, -0.5f, 0.2f]));
        Assert.Equal(0f, RecordingTracks.Peak([]));
    }
}

/// <summary>全屏录屏的取景:鼠标所在显示器,边框画在屏幕里面(ADR-0030)。</summary>
public class FullScreenGeometryTests
{
    [Fact]
    public void 边框贴着屏幕边缘画在里面()
    {
        var bars = ScrollCaptureLayout.InnerBorder(new PxRect(100, 0, 1920, 1080), 3);

        Assert.Equal(new PxRect(100, 0, 1920, 3), bars[0]);
        Assert.Equal(new PxRect(100, 1077, 1920, 3), bars[1]);
        Assert.Equal(new PxRect(100, 3, 3, 1074), bars[2]);
        Assert.Equal(new PxRect(2017, 3, 3, 1074), bars[3]);
    }
}

/// <summary>
/// 画面文件 + 单独的声音文件 → 转封装合成,真用 Media Foundation(不读屏幕、不开窗口)。
/// 2026-11-12 真机发现从多轨文件按流编号挑会挑错(成品没有画面),所以改成每条轨一个文件。
/// </summary>
public sealed class Mp4RemuxerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-remux-tests", Guid.NewGuid().ToString("N"));

    public Mp4RemuxerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static float[] Tone()
    {
        var tone = new float[AudioMixer.SampleRate / 10 * 2];
        for (var i = 0; i < tone.Length / 2; i++)
        {
            tone[i * 2] = tone[i * 2 + 1] = 0.3f * MathF.Sin(i * 2 * MathF.PI * 440 / AudioMixer.SampleRate);
        }

        return tone;
    }

    /// <summary>2 秒画面(先试硬件编码,和真实录制一样)。</summary>
    private string WriteVideo()
    {
        var path = Path.Combine(_dir, "video (录制中).mp4");
        var frame = new byte[640 * 360 * 4];
        Array.Fill(frame, (byte)128);
        using var writer = Mp4Writer.Create(path, 640, 360, 30, audioTracks: 0, preferHardware: true, fragmented: true);
        for (var i = 0; i < 60; i++)
        {
            writer.WriteVideo(frame, TimeSpan.FromSeconds(i / 30.0), TimeSpan.FromSeconds(1 / 30.0));
        }

        writer.Finish();
        return path;
    }

    /// <summary>只有声音的文件,长 <paramref name="tenths"/> / 10 秒。</summary>
    private static string WriteAudio(string path, int tenths)
    {
        using var writer = Mp4Writer.CreateAudioOnly(path, fragmented: true);
        var tone = Tone();
        for (var i = 0; i < tenths; i++)
        {
            writer.WriteAudio(tone, TimeSpan.FromSeconds(i / 10.0));
        }

        writer.Finish();
        return path;
    }

    [Fact]
    public async Task 画面加选中的声音文件_成品有画面和一条声音_时长取最长的那条()
    {
        var video = WriteVideo();
        var system = WriteAudio(RecordingTracks.SidecarPath(video, AudioTrackKind.System), 30);
        var output = Path.Combine(_dir, "out.mp4");

        Mp4Remuxer.Remux(output, [video, system]);

        Assert.Equal(1, Mp4Remuxer.StreamCount(video));
        Assert.Equal(1, Mp4Remuxer.StreamCount(system));
        Assert.Equal(2, Mp4Remuxer.StreamCount(output));
        var properties = await (await StorageFile.GetFileFromPathAsync(output)).Properties.GetVideoPropertiesAsync();
        Assert.Equal((640u, 360u), (properties.Width, properties.Height));
        Assert.InRange(properties.Duration.TotalSeconds, 2.8, 3.2); // 3 秒的声音确实进去了
    }

    [Fact]
    public async Task 一条声音都不留_只有画面()
    {
        var video = WriteVideo();
        var output = Path.Combine(_dir, "silent.mp4");

        Mp4Remuxer.Remux(output, [video]);

        Assert.Equal(1, Mp4Remuxer.StreamCount(output));
        var properties = await (await StorageFile.GetFileFromPathAsync(output)).Properties.GetVideoPropertiesAsync();
        Assert.Equal((640u, 360u), (properties.Width, properties.Height));
        Assert.InRange(properties.Duration.TotalSeconds, 1.8, 2.2);
    }

    [Fact]
    public void 声音临时文件和画面临时文件放在一起()
    {
        Assert.Equal(
            Path.Combine("D:\v", "WAIO 录屏 (录制中) 麦克风.mp4"),
            RecordingTracks.SidecarPath(Path.Combine("D:\v", "WAIO 录屏 (录制中).mp4"), AudioTrackKind.Microphone));
    }
}
