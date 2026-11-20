using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Recording;

namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>
/// 一次录屏的后台部分(MAIO RecordingSession 的写入侧,ADR-0021):专用线程按帧率截选区,
/// MP4 只写有变化的帧(帧率可变,静止画面至少每秒写一帧),声音按采集时刻混成一条音轨(全屏录屏另外每路单独一条,ADR-0030);GIF 边录边编。
/// <see cref="Start"/>、<see cref="Stop"/> 可以在任意线程调用;编码器只在录制线程上碰。
/// </summary>
internal sealed class ScreenRecorder
{
    /// <param name="Rect">截取的区域(全局物理像素;MP4 时宽高已经是偶数)。</param>
    /// <param name="Output">输出的宽高(比 Rect 小时每帧缩小)。</param>
    /// <param name="SeparateTracks">每路声音单独一条音轨,两路都开时再加一条混音(全屏录屏,ADR-0030)。</param>
    /// <param name="Fragmented">写分段 MP4(中途崩溃已写下的部分也能播放)。</param>
    /// <param name="Monitor">不为 0 时先试 GPU 取帧(<see cref="GpuScreenGrabber"/>,全屏录屏),不行再用 BitBlt。</param>
    internal sealed record Options(
        PxRect Rect, (int Width, int Height) Output, RecordingFormat Format, int Fps, bool SystemAudio, bool Microphone, bool Cursor, string Path,
        bool SeparateTracks = false, bool Fragmented = false, nint Monitor = 0, PxRect MonitorBounds = default);

    /// <summary>静止画面也至少这么久写一帧(有的播放器遇到很长的帧间隔会卡住进度条)。</summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);

    private readonly Options _options;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _audioGate = new();
    private readonly List<AudioCapture> _captures = [];
    /// <summary>每条音轨一个混音器;<c>Sources</c> 是这条音轨用到的声音来源(0 = 系统声音,1 = 麦克风),顺序即混音器的输入编号。</summary>
    private readonly List<(AudioMixer Mixer, int[] Sources)> _tracks = [];
    private readonly float[] _peaks = new float[2];

    /// <summary>分轨录制时每条音轨一个只有声音的 MP4(和 <see cref="AudioLayout"/> 一一对应);不分轨时为空,声音写进画面那个文件。</summary>
    private readonly List<Mp4Writer> _audioWriters = [];
    private long _start; // 录制开始的时刻(QPC,100 ns)
    private Exception? _error;
    private int _frames;
    private int _dropped;

    private ScreenRecorder(Options options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "WAIO.Recording", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>开始后没能打开的声音设备(录制照常进行,只是没有这一路声音)。</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>录制中出错时调用(在录制线程上);录制已经停下。</summary>
    public event Action<Exception>? Failed;

    /// <summary>录了哪些音轨。不分轨时在画面那个 MP4 里(视频轨之后);分轨时每条一个文件,见 <see cref="RecordingTracks.SidecarPath"/>。</summary>
    public IReadOnlyList<AudioTrackKind> AudioLayout { get; private set; } = [];

    /// <summary>录到的系统声音 / 麦克风的最大音量(0~1),用来判断"全程没声音"。</summary>
    public (float System, float Microphone) Peaks
    {
        get
        {
            lock (_audioGate)
            {
                return (_peaks[0], _peaks[1]);
            }
        }
    }

    public TimeSpan Elapsed => _start == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(AudioCapture.Now() - _start);

    /// <summary>开始录制:编码器建好、第一帧写下之后才返回。建不起来时抛 <see cref="RecordingException"/>。</summary>
    public static ScreenRecorder Start(Options options, ILogger logger)
    {
        var recorder = new ScreenRecorder(options, logger);
        recorder._thread.Start();
        recorder._ready.Wait();
        if (recorder._error is { } error)
        {
            recorder._thread.Join();
            recorder.DeleteFiles();
            throw error as RecordingException ?? new RecordingException(error.Message);
        }

        return recorder;
    }

    /// <summary>停止并写完文件。<paramref name="keep"/> 为 false 时删除文件(放弃)。返回录了多久。</summary>
    public Task<TimeSpan> StopAsync(bool keep) => Task.Run(() =>
    {
        _stop.Set();
        _thread.Join();
        if (!keep || _error is not null)
        {
            DeleteFiles();
        }

        if (_error is { } error && keep)
        {
            throw error as RecordingException ?? new RecordingException(error.Message);
        }

        return Elapsed;
    });

    private void Run()
    {
        IFrameGrabber? grabber = null;
        Mp4Writer? mp4 = null;
        GifEncoder? gif = null;
        FileStream? gifFile = null;
        try
        {
            grabber = CreateGrabber();
            var (width, height) = _options.Output;
            var audio = _options.Format == RecordingFormat.Mp4 && (_options.SystemAudio || _options.Microphone);
            if (_options.Format == RecordingFormat.Mp4)
            {
                AudioLayout = audio ? RecordingTracks.Layout(_options.SystemAudio, _options.Microphone, _options.SeparateTracks) : [];
                var embedded = _options.SeparateTracks ? 0 : AudioLayout.Count;
                mp4 = Mp4Writer.Create(_options.Path, width, height, _options.Fps, embedded, preferHardware: true, _options.Fragmented);
                if (_options.SeparateTracks)
                {
                    foreach (var kind in AudioLayout)
                    {
                        _audioWriters.Add(Mp4Writer.CreateAudioOnly(RecordingTracks.SidecarPath(_options.Path, kind), _options.Fragmented));
                    }
                }

                _logger.LogInformation("录屏开始:{Width}×{Height} MP4 {Fps} 帧/秒,{Encoder}编码,音轨 [{Tracks}]",
                    width, height, _options.Fps, mp4.Hardware ? "硬件" : "软件", string.Join(", ", AudioLayout));
            }
            else
            {
                gifFile = new FileStream(_options.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                gif = new GifEncoder(gifFile, width, height);
                _logger.LogInformation("录屏开始:{Width}×{Height} GIF {Fps} 帧/秒", width, height, _options.Fps);
            }

            _start = AudioCapture.Now();
            if (audio)
            {
                StartAudio();
            }

            Loop(grabber, mp4, gif);
        }
        catch (Exception ex)
        {
            _error = ex;
            _logger.LogError(ex, "录屏失败");
            if (_ready.IsSet)
            {
                Failed?.Invoke(ex);
            }
        }
        finally
        {
            foreach (var capture in _captures)
            {
                capture.Dispose();
            }

            mp4?.Dispose();
            foreach (var writer in _audioWriters)
            {
                writer.Dispose();
            }

            gif?.Dispose();
            gifFile?.Dispose();
            grabber?.Dispose();
            _ready.Set();
            _logger.LogInformation("录屏结束:{Elapsed:F1} 秒,写了 {Frames} 帧,来不及截而跳过 {Dropped} 次", Elapsed.TotalSeconds, _frames, _dropped);
        }
    }

    private IFrameGrabber CreateGrabber()
    {
        if (_options.Monitor != 0)
        {
            try
            {
                var gpu = new GpuScreenGrabber(_options.Monitor, _options.MonitorBounds, _options.Rect, _options.Cursor);
                _logger.LogInformation("录屏取帧:GPU(Windows.Graphics.Capture)");
                return gpu;
            }
            catch (RecordingException ex)
            {
                _logger.LogWarning("GPU 取帧不可用,改用 BitBlt:{Message}", ex.Message);
            }
        }

        return new ScreenGrabber(_options.Rect, _options.Cursor);
    }

    private void StartAudio()
    {
        // 来源编号:0 = 系统声音,1 = 麦克风
        var enabled = new List<int>();
        if (_options.SystemAudio)
        {
            enabled.Add(0);
        }

        if (_options.Microphone)
        {
            enabled.Add(1);
        }

        foreach (var kind in AudioLayout)
        {
            int[] sources = kind switch
            {
                AudioTrackKind.System => [0],
                AudioTrackKind.Microphone => [1],
                _ => [.. enabled],
            };
            _tracks.Add((new AudioMixer(sources.Length), sources));
        }

        var warnings = new List<string>();
        foreach (var source in enabled)
        {
            try
            {
                _captures.Add(AudioCapture.Start(loopback: source == 0, (qpc, samples) =>
                {
                    var frame = (qpc - _start) * AudioMixer.SampleRate / 10_000_000;
                    var peak = RecordingTracks.Peak(samples);
                    lock (_audioGate)
                    {
                        _peaks[source] = Math.Max(_peaks[source], peak);
                        foreach (var (mixer, inputs) in _tracks)
                        {
                            var input = Array.IndexOf(inputs, source);
                            if (input >= 0)
                            {
                                mixer.Push(input, frame, samples);
                            }
                        }
                    }
                }));
            }
            catch (RecordingException ex)
            {
                // 这一路没有数据,混音器会在等待时间过后当作静音
                warnings.Add(ex.Message);
                _logger.LogWarning("录屏时打开{Source}失败:{Message}", source == 0 ? "系统声音" : "麦克风", ex.Message);
            }
        }

        Warnings = warnings;
    }

    private void Loop(IFrameGrabber grabber, Mp4Writer? mp4, GifEncoder? gif)
    {
        var interval = TimeSpan.FromSeconds(1.0 / _options.Fps);
        var resize = (grabber.Width, grabber.Height) != _options.Output;
        byte[]? last = null;
        var lastWritten = TimeSpan.MinValue;
        var due = TimeSpan.Zero;
        while (true)
        {
            var now = Elapsed;
            if (due > now && _stop.Wait(due - now))
            {
                break;
            }

            if (_stop.IsSet)
            {
                break;
            }

            now = Elapsed;
            due += interval;
            if (due < now)
            {
                _dropped++;
                due = now + interval; // 跟不上:跳过,不补帧
            }

            var pixels = grabber.Grab();
            var changed = last is null || !pixels.SequenceEqual(last);
            if (changed)
            {
                last ??= new byte[pixels.Length];
                pixels.CopyTo(last);
            }

            if (mp4 is not null)
            {
                if (changed || now - lastWritten >= KeepAlive)
                {
                    mp4.WriteVideo(Output(last!, grabber, resize), now, interval);
                    lastWritten = now;
                    _frames++;
                }

                WriteAudio(mp4, Elapsed);
            }
            else if (changed)
            {
                gif!.AddFrame(Output(last!, grabber, resize), now);
                _frames++;
            }

            if (!_ready.IsSet)
            {
                _ready.Set(); // 第一帧写下了,开始计时显示
            }
        }

        var end = Elapsed;
        if (mp4 is not null)
        {
            // 最后一帧显示到停止的时刻(MAIO endSession)
            if (last is not null && end - lastWritten > interval)
            {
                mp4.WriteVideo(Output(last, grabber, resize), end - interval, interval);
            }

            WriteAudio(mp4, end, flush: true);
            mp4.Finish();
            foreach (var writer in _audioWriters)
            {
                writer.Finish();
            }
        }
        else
        {
            gif!.Finish(end);
        }
    }

    private void WriteAudio(Mp4Writer mp4, TimeSpan now, bool flush = false)
    {
        var frame = now.Ticks * AudioMixer.SampleRate / 10_000_000;
        for (var track = 0; track < _tracks.Count; track++)
        {
            var mixer = _tracks[track].Mixer;
            float[] mixed;
            long start;
            lock (_audioGate)
            {
                start = mixer.MixedFrames;
                mixed = flush ? mixer.Flush(frame) : mixer.Mix(frame);
            }

            if (mixed.Length > 0)
            {
                var time = TimeSpan.FromTicks(start * 10_000_000 / AudioMixer.SampleRate);
                if (_audioWriters.Count > 0)
                {
                    _audioWriters[track].WriteAudio(mixed, time);
                }
                else
                {
                    mp4.WriteAudio(track, mixed, time);
                }
            }
        }
    }

    private byte[] Output(byte[] frame, IFrameGrabber grabber, bool resize)
    {
        if (!resize)
        {
            return frame;
        }

        var (width, height) = _options.Output;
        return LongImageBuilder.Resize(new PixelBuffer(grabber.Width, grabber.Height, frame), width, height).Pixels;
    }

    /// <summary>删掉这次录制写的全部文件(画面和分轨的声音)。</summary>
    public void DeleteFiles()
    {
        TryDelete(_options.Path);
        foreach (var kind in AudioLayout)
        {
            TryDelete(RecordingTracks.SidecarPath(_options.Path, kind));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 放弃时删不掉就留着
        }
    }
}
