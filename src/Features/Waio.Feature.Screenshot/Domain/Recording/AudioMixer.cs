namespace Waio.Feature.Screenshot.Domain.Recording;

/// <summary>
/// 把几路声音(系统声音、麦克风)混成一条立体声(MAIO AudioMixer,ADR-0021)。纯逻辑,不是线程安全的(调用方加锁)。
/// <list type="bullet">
/// <item>输入已经统一成 48 kHz 浮点立体声(交错),按"从录制开始第几个采样帧"对齐;</item>
/// <item>相邻两段间隔 ≤ 2 ms 视为连续(时钟抖动),更大的缺口补静音;和已有数据重叠的部分丢掉;</item>
/// <item>所有路都写到的部分立即混出;某一路一直没有数据(系统没在放声音时回环采集不出数据)时最多等 <see cref="MaxWaitFrames"/>,之后当作静音;</item>
/// <item>相加后限制在 -1~1。</item>
/// </list>
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>2 ms 以内的间隔当作连续。</summary>
    public const int ContiguousFrames = SampleRate / 500;

    /// <summary>某一路最多等 0.2 秒。</summary>
    public const int MaxWaitFrames = SampleRate / 5;

    private readonly Track[] _tracks;
    private long _mixed; // 已经混出的采样帧数

    public AudioMixer(int sources)
    {
        if (sources <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sources));
        }

        _tracks = [.. Enumerable.Range(0, sources).Select(_ => new Track())];
    }

    /// <summary>已经混出的采样帧数(= 输出音轨的当前长度)。</summary>
    public long MixedFrames => _mixed;

    /// <summary>
    /// 第 <paramref name="source"/> 路从第 <paramref name="startFrame"/> 帧开始的一段声音(交错立体声,长度是 2 的倍数)。
    /// <paramref name="startFrame"/> 可以是负数(录制开始前就采到的),负的部分丢掉。
    /// </summary>
    public void Push(int source, long startFrame, ReadOnlySpan<float> samples)
    {
        var track = _tracks[source];
        var frames = samples.Length / Channels;
        var end = track.End;
        if (startFrame > end && startFrame - end <= ContiguousFrames)
        {
            startFrame = end; // 时钟抖动:接在后面
        }

        if (startFrame > end)
        {
            track.AppendSilence(startFrame - end);
        }
        else if (startFrame < end)
        {
            // 重叠(包括已经被当成静音混出去的部分):丢掉重叠的那一段
            var skip = (int)Math.Min(frames, end - startFrame);
            samples = samples[(skip * Channels)..];
        }

        track.Append(samples);
    }

    /// <summary>
    /// 混出能确定的部分。<paramref name="nowFrame"/> 是当前时刻(从录制开始的采样帧数):
    /// 落后超过 <see cref="MaxWaitFrames"/> 的路补静音。返回混好的交错立体声(可能为空)。
    /// </summary>
    public float[] Mix(long nowFrame) => MixUntil(Math.Max(_tracks.Min(t => t.End), nowFrame - MaxWaitFrames));

    /// <summary>结束:把所有路都补齐到 <paramref name="endFrame"/>,全部混出。</summary>
    public float[] Flush(long endFrame) => MixUntil(Math.Max(endFrame, _tracks.Max(t => t.End)));

    private float[] MixUntil(long limit)
    {
        if (limit <= _mixed)
        {
            return [];
        }

        var frames = (int)(limit - _mixed);
        var output = new float[frames * Channels];
        foreach (var track in _tracks)
        {
            if (track.End < limit)
            {
                track.AppendSilence(limit - track.End);
            }

            track.AddTo(output, frames);
        }

        for (var i = 0; i < output.Length; i++)
        {
            output[i] = Math.Clamp(output[i], -1f, 1f);
        }

        _mixed = limit;
        return output;
    }

    /// <summary>一路声音:从"已混出位置"开始、还没混出的采样。</summary>
    private sealed class Track
    {
        private float[] _buffer = new float[SampleRate * Channels];
        private int _count; // 采样数(不是帧数)
        private long _start; // _buffer[0] 对应的帧

        public long End => _start + _count / Channels;

        public void AppendSilence(long frames)
        {
            var samples = (int)Math.Min(frames * Channels, int.MaxValue / 2);
            Ensure(samples);
            Array.Clear(_buffer, _count, samples);
            _count += samples;
        }

        public void Append(ReadOnlySpan<float> samples)
        {
            var even = samples.Length / Channels * Channels;
            Ensure(even);
            samples[..even].CopyTo(_buffer.AsSpan(_count));
            _count += even;
        }

        /// <summary>把前 <paramref name="frames"/> 帧加到 <paramref name="output"/> 上并移除。</summary>
        public void AddTo(float[] output, int frames)
        {
            var samples = frames * Channels;
            for (var i = 0; i < samples; i++)
            {
                output[i] += _buffer[i];
            }

            Array.Copy(_buffer, samples, _buffer, 0, _count - samples);
            _count -= samples;
            _start += frames;
        }

        private void Ensure(int more)
        {
            if (_count + more > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + more));
            }
        }
    }
}

/// <summary>采集到的声音格式转换:任意声道数的 16 位整数 / 32 位浮点 → 立体声浮点,必要时线性插值重采样到 48 kHz。</summary>
public static class AudioConvert
{
    /// <summary>交错的多声道 → 立体声:单声道复制到两边;多于两声道只取前两个(左、右)。</summary>
    public static float[] ToStereo(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels == 2)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var output = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var left = interleaved[i * channels];
            output[i * 2] = left;
            output[i * 2 + 1] = channels == 1 ? left : interleaved[i * channels + 1];
        }

        return output;
    }

    public static float[] FromInt16(ReadOnlySpan<byte> pcm)
    {
        var output = new float[pcm.Length / 2];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
        }

        return output;
    }

    /// <summary>立体声重采样(线性插值;只用于设备不支持自动转换时的退路)。</summary>
    public static float[] Resample(float[] stereo, int fromRate, int toRate)
    {
        if (fromRate == toRate || stereo.Length < 4)
        {
            return stereo;
        }

        var frames = stereo.Length / 2;
        var outFrames = (int)((long)frames * toRate / fromRate);
        var output = new float[outFrames * 2];
        for (var i = 0; i < outFrames; i++)
        {
            var position = (double)i * fromRate / toRate;
            var index = Math.Min((int)position, frames - 2);
            var t = (float)(position - index);
            for (var c = 0; c < 2; c++)
            {
                output[i * 2 + c] = stereo[index * 2 + c] * (1 - t) + stereo[(index + 1) * 2 + c] * t;
            }
        }

        return output;
    }

    /// <summary>浮点 → 16 位整数 PCM(AAC 编码器的输入)。</summary>
    public static byte[] ToInt16(ReadOnlySpan<float> samples)
    {
        var output = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)value;
            output[i * 2 + 1] = (byte)(value >> 8);
        }

        return output;
    }
}
