using Waio.Feature.Screenshot.Domain.Recording;

namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>
/// 用 Media Foundation 的 Sink Writer 写 MP4(ADR-0021):视频 H.264(输入 BGRA,由 Sink Writer 自己转成编码器要的格式),
/// 声音 AAC 48 kHz 立体声 192 kbps。先用硬件编码,建不起来就退回系统自带的软件编码。
/// 帧率可变:只写有变化的帧,时间戳按实际截图时刻。不是线程安全的:所有调用都要在同一个线程上。
/// </summary>
internal sealed unsafe class Mp4Writer : IDisposable
{
    private const long Ticks = 10_000_000; // Media Foundation 的时间单位是 100 ns

    private nint _writer;
    private readonly uint _video;
    private readonly uint[] _audio;
    private readonly int _width;
    private readonly int _height;
    private bool _finished;

    private Mp4Writer(nint writer, uint video, uint[] audio, int width, int height, bool hardware)
    {
        _writer = writer;
        _video = video;
        _audio = audio;
        _width = width;
        _height = height;
        Hardware = hardware;
    }

    /// <summary>用的是不是硬件编码。</summary>
    public bool Hardware { get; }

    public bool HasAudio => _audio.Length > 0;

    /// <summary>音轨数。</summary>
    public int AudioTracks => _audio.Length;

    /// <summary>
    /// 建一个写到 <paramref name="path"/> 的 MP4(宽高必须是偶数)。<paramref name="preferHardware"/> 为 true 时先试硬件编码。
    /// </summary>
    public static Mp4Writer Create(string path, int width, int height, int fps, bool audio, bool preferHardware = true) =>
        Create(path, width, height, fps, audio ? 1 : 0, preferHardware, fragmented: false);

    /// <summary>
    /// 带 <paramref name="audioTracks"/> 条音轨的 MP4。<paramref name="fragmented"/> = 分段 MP4(全屏录屏的临时文件,ADR-0030):
    /// 写到一半进程没了,已经写下的部分也能播放。
    /// </summary>
    public static Mp4Writer Create(string path, int width, int height, int fps, int audioTracks, bool preferHardware, bool fragmented)
    {
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException($"H.264 的宽高必须是偶数:{width}×{height}");
        }

        Com.EnsureMta();
        Com.Check(Mf.MFStartup(Mf.Version, 0), "启动 Media Foundation");
        if (preferHardware)
        {
            try
            {
                return Open(path, width, height, fps, audioTracks, fragmented, hardware: true);
            }
            catch (RecordingException)
            {
                // 退回软件编码
            }
        }

        return Open(path, width, height, fps, audioTracks, fragmented, hardware: false);
    }

    /// <summary>只有一条声音的 MP4(全屏录屏每路声音单独一个临时文件,ADR-0030)。</summary>
    public static Mp4Writer CreateAudioOnly(string path, bool fragmented)
    {
        Com.EnsureMta();
        Com.Check(Mf.MFStartup(Mf.Version, 0), "启动 Media Foundation");
        return Open(path, 0, 0, 0, 1, fragmented, hardware: false, video: false);
    }

    private static Mp4Writer Open(string path, int width, int height, int fps, int audioTracks, bool fragmented, bool hardware, bool video = true)
    {
        nint attributes = 0, writer = 0, videoOut = 0, videoIn = 0, audioOut = 0, audioIn = 0;
        try
        {
            Com.Check(Mf.MFCreateAttributes(out attributes, 3), "创建编码参数");
            Com.Check(Mf.SetUint32(attributes, Mf.EnableHardwareTransforms, hardware ? 1u : 0u), "设置硬件编码");
            if (fragmented)
            {
                Com.Check(Mf.SetGuid(attributes, Mf.TranscodeContainerType, Mf.ContainerFragmentedMp4), "设置分段 MP4");
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Com.Check(Mf.MFCreateSinkWriterFromURL(path, 0, attributes, out writer), "创建 MP4 文件");

            var videoStream = uint.MaxValue;
            if (video)
            {
                videoOut = VideoType(Mf.VideoH264, width, height, fps);
                Com.Check(Mf.SetUint32(videoOut, Mf.AvgBitrate, (uint)RecordingGeometry.Bitrate(width, height, fps)), "设置码率");
                Com.Check(Mf.SetUint32(videoOut, Mf.Mpeg2Profile, 100 /* eAVEncH264VProfile_High */), "设置 H.264 档次");
                Com.Check(Mf.AddStream(writer, videoOut, out videoStream), "添加视频轨");

                // 输入 BGRA(MFVideoFormat_RGB32),正的步长 = 从上到下逐行
                videoIn = VideoType(Mf.VideoRgb32, width, height, fps);
                Com.Check(Mf.SetUint32(videoIn, Mf.DefaultStride, (uint)(width * 4)), "设置步长");
                Com.Check(Mf.SetInputMediaType(writer, videoStream, videoIn), "设置视频输入格式");
            }

            var audioStreams = new uint[audioTracks];
            if (audioTracks > 0)
            {
                audioOut = AudioType(Mf.AudioAac, bytesPerSecond: 24_000); // 192 kbps
                audioIn = AudioType(Mf.AudioPcm, bytesPerSecond: AudioMixer.SampleRate * 4);
                Com.Check(Mf.SetUint32(audioIn, Mf.AudioBlockAlignment, 4), "设置声音格式");
                Com.Check(Mf.SetUint32(audioIn, Mf.AllSamplesIndependent, 1), "设置声音格式");
                for (var i = 0; i < audioTracks; i++)
                {
                    Com.Check(Mf.AddStream(writer, audioOut, out audioStreams[i]), "添加音轨");
                    Com.Check(Mf.SetInputMediaType(writer, audioStreams[i], audioIn), "设置声音输入格式");
                }
            }

            Com.Check(Mf.BeginWriting(writer), "开始写 MP4");
            var result = new Mp4Writer(writer, videoStream, audioStreams, width, height, hardware);
            writer = 0; // 交给结果
            return result;
        }
        finally
        {
            Com.Release(ref attributes);
            Com.Release(ref writer);
            Com.Release(ref videoOut);
            Com.Release(ref videoIn);
            Com.Release(ref audioOut);
            Com.Release(ref audioIn);
        }
    }

    private static nint VideoType(in Guid subtype, int width, int height, int fps)
    {
        Com.Check(Mf.MFCreateMediaType(out var type), "创建视频格式");
        Com.Check(Mf.SetGuid(type, Mf.MajorType, Mf.MediaTypeVideo), "设置视频格式");
        Com.Check(Mf.SetGuid(type, Mf.Subtype, subtype), "设置视频格式");
        Com.Check(Mf.SetUint32(type, Mf.InterlaceMode, 2 /* MFVideoInterlace_Progressive */), "设置逐行扫描");
        Com.Check(Mf.SetUint64(type, Mf.FrameSize, Mf.Pack((uint)width, (uint)height)), "设置画面大小");
        Com.Check(Mf.SetUint64(type, Mf.FrameRate, Mf.Pack((uint)fps, 1)), "设置帧率");
        Com.Check(Mf.SetUint64(type, Mf.PixelAspectRatio, Mf.Pack(1, 1)), "设置像素宽高比");
        return type;
    }

    private static nint AudioType(in Guid subtype, int bytesPerSecond)
    {
        Com.Check(Mf.MFCreateMediaType(out var type), "创建声音格式");
        Com.Check(Mf.SetGuid(type, Mf.MajorType, Mf.MediaTypeAudio), "设置声音格式");
        Com.Check(Mf.SetGuid(type, Mf.Subtype, subtype), "设置声音格式");
        Com.Check(Mf.SetUint32(type, Mf.AudioBitsPerSample, 16), "设置声音格式");
        Com.Check(Mf.SetUint32(type, Mf.AudioSampleRate, AudioMixer.SampleRate), "设置采样率");
        Com.Check(Mf.SetUint32(type, Mf.AudioChannels, AudioMixer.Channels), "设置声道数");
        Com.Check(Mf.SetUint32(type, Mf.AudioAvgBytesPerSecond, (uint)bytesPerSecond), "设置声音码率");
        return type;
    }

    /// <summary>写一帧(BGRA,从上到下,宽 × 高 × 4 字节)。<paramref name="time"/>、<paramref name="duration"/> 从录制开始算。</summary>
    public void WriteVideo(ReadOnlySpan<byte> bgra, TimeSpan time, TimeSpan duration)
    {
        if (_video == uint.MaxValue)
        {
            throw new InvalidOperationException("这个 MP4 没有视频轨");
        }

        var length = _width * _height * 4;
        if (bgra.Length < length)
        {
            throw new ArgumentException("画面大小不对");
        }

        WriteSample(_video, bgra[..length], time, duration);
    }

    /// <summary>写一段声音(48 kHz 浮点立体声交错)。</summary>
    public void WriteAudio(ReadOnlySpan<float> stereo, TimeSpan time) => WriteAudio(0, stereo, time);

    /// <summary>写一段声音到第 <paramref name="track"/> 条音轨(从 0 开始)。</summary>
    public void WriteAudio(int track, ReadOnlySpan<float> stereo, TimeSpan time)
    {
        if (track >= _audio.Length || stereo.Length < 2)
        {
            return;
        }

        var frames = stereo.Length / 2;
        WriteSample(_audio[track], AudioConvert.ToInt16(stereo), time, TimeSpan.FromTicks(frames * Ticks / AudioMixer.SampleRate));
    }

    private void WriteSample(uint stream, ReadOnlySpan<byte> data, TimeSpan time, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_writer == 0 || _finished, this);
        nint buffer = 0, sample = 0;
        try
        {
            Com.Check(Mf.MFCreateMemoryBuffer((uint)data.Length, out buffer), "分配编码缓冲区");
            Com.Check(Mf.Lock(buffer, out var pointer), "锁定缓冲区");
            data.CopyTo(new Span<byte>(pointer, data.Length));
            Com.Check(Mf.Unlock(buffer), "解锁缓冲区");
            Com.Check(Mf.SetCurrentLength(buffer, (uint)data.Length), "设置缓冲区长度");
            Com.Check(Mf.MFCreateSample(out sample), "创建样本");
            Com.Check(Mf.AddBuffer(sample, buffer), "创建样本");
            Com.Check(Mf.SetSampleTime(sample, time.Ticks), "设置时间戳");
            Com.Check(Mf.SetSampleDuration(sample, Math.Max(1, duration.Ticks)), "设置时长");
            Com.Check(Mf.WriteSample(_writer, stream, sample), stream == _video ? "编码画面" : "编码声音");
        }
        finally
        {
            Com.Release(ref sample);
            Com.Release(ref buffer);
        }
    }

    /// <summary>写完文件(等编码器把剩下的帧编完)。之后不能再写。</summary>
    public void Finish()
    {
        if (_finished || _writer == 0)
        {
            return;
        }

        _finished = true;
        Com.Check(Mf.Finalize(_writer), "完成 MP4");
    }

    public void Dispose() => Com.Release(ref _writer);
}
