using System.Diagnostics;
using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain.Recording;

namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>
/// WASAPI 共享模式采集(ADR-0021):系统声音用默认输出设备的回环(loopback),麦克风用默认输入设备。
/// 优先让系统直接转换成 48 kHz 浮点立体声(<c>AUTOCONVERTPCM</c>);设备不支持时用它自己的格式,在这里转换。
/// 专用线程每 10 ms 取一次数据,连同"这段声音开始的时刻"(QPC,100 ns)交给 <c>onData</c>。
/// 系统没在放声音时回环采集不出数据,由混音器补静音(<see cref="AudioMixer"/>)。
/// </summary>
internal sealed unsafe class AudioCapture : IDisposable
{
    private static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private const uint Loopback = 0x00020000, AutoConvert = 0x80000000, SrcDefaultQuality = 0x08000000;
    private const uint BufferSilent = 0x2;
    private const int NotFound = unchecked((int)0x80070490);

    private readonly Action<long, float[]> _onData;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _stop;
    private Exception? _startError;

    private AudioCapture(bool loopback, Action<long, float[]> onData)
    {
        IsLoopback = loopback;
        _onData = onData;
        _thread = new Thread(Run) { IsBackground = true, Name = loopback ? "WAIO.Recording.SystemAudio" : "WAIO.Recording.Microphone" };
    }

    public bool IsLoopback { get; }

    /// <summary>
    /// 开始采集。<paramref name="onData"/> 在采集线程上调用:(这段声音开始的时刻,100 ns QPC;48 kHz 浮点立体声交错)。
    /// 设备不存在、被隐私设置禁止时抛 <see cref="RecordingException"/>。
    /// </summary>
    public static AudioCapture Start(bool loopback, Action<long, float[]> onData)
    {
        var capture = new AudioCapture(loopback, onData);
        capture._thread.Start();
        capture._ready.Wait();
        if (capture._startError is { } error)
        {
            capture.Dispose();
            throw error is RecordingException ? error : new RecordingException(error.Message);
        }

        return capture;
    }

    /// <summary>现在(QPC,100 ns),和 WASAPI 给的时间戳同一个时钟。</summary>
    public static long Now() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Run()
    {
        nint enumerator = 0, device = 0, client = 0, capture = 0;
        nint mixFormat = 0;
        try
        {
            Com.EnsureMta();
            Com.Check(Com.CoCreateInstance(ClsidEnumerator, 0, 0x17, IidEnumerator, out enumerator), "打开声音设备列表");
            nint found;
            var hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Com.Vtbl(enumerator)[4])(enumerator, IsLoopback ? 0 : 1, 0, &found);
            if (hr == NotFound)
            {
                throw new RecordingException(IsLoopback ? "没有找到声音输出设备" : "没有找到麦克风", hr);
            }

            Com.Check(hr, IsLoopback ? "打开声音输出设备" : "打开麦克风");
            device = found;
            nint activated;
            fixed (Guid* iid = &IidAudioClient)
            {
                Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Com.Vtbl(device)[3])(device, iid, 0x17, 0, &activated),
                    "打开声音设备");
            }

            client = activated;
            var format = Initialize(client, IsLoopback, ref mixFormat);
            nint service;
            fixed (Guid* iid = &IidCaptureClient)
            {
                Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Vtbl(client)[14])(client, iid, &service), "开始采集声音");
            }

            capture = service;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, int>)Com.Vtbl(client)[10])(client), "开始采集声音");
            _ready.Set();
            Loop(capture, format);
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)Com.Vtbl(client)[11])(client); // Stop
        }
        catch (Exception ex)
        {
            if (!_ready.IsSet)
            {
                _startError = ex.HResult == unchecked((int)0x80070005)
                    ? new RecordingException("没有权限使用麦克风:请在 Windows 设置 → 隐私和安全性 → 麦克风里允许桌面应用访问", ex.HResult)
                    : ex;
            }
        }
        finally
        {
            if (mixFormat != 0)
            {
                Com.CoTaskMemFree(mixFormat);
            }

            Com.Release(ref capture);
            Com.Release(ref client);
            Com.Release(ref device);
            Com.Release(ref enumerator);
            _ready.Set();
        }
    }

    /// <summary>设备给的格式。</summary>
    private readonly record struct Format(int Channels, int SampleRate, bool IsFloat, int BytesPerFrame);

    /// <summary>先要求 48 kHz 浮点立体声(系统转换);失败时用设备自己的格式。</summary>
    private static Format Initialize(nint client, bool loopback, ref nint mixFormat)
    {
        var flags = loopback ? Loopback : 0;
        var wanted = stackalloc byte[18];
        *(ushort*)wanted = 3; // WAVE_FORMAT_IEEE_FLOAT
        *(ushort*)(wanted + 2) = AudioMixer.Channels;
        *(uint*)(wanted + 4) = AudioMixer.SampleRate;
        *(uint*)(wanted + 8) = AudioMixer.SampleRate * 8;
        *(ushort*)(wanted + 12) = 8;
        *(ushort*)(wanted + 14) = 32;
        *(ushort*)(wanted + 16) = 0;
        var initialize = (delegate* unmanaged[Stdcall]<nint, int, uint, long, long, byte*, nint, int>)Com.Vtbl(client)[3];
        if (initialize(client, 0, flags | AutoConvert | SrcDefaultQuality, 2_000_000, 0, wanted, 0) >= 0)
        {
            return new Format(AudioMixer.Channels, AudioMixer.SampleRate, true, 8);
        }

        nint mix;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Vtbl(client)[8])(client, &mix), "读取声音格式");
        mixFormat = mix;
        var p = (byte*)mix;
        var tag = *(ushort*)p;
        var channels = *(ushort*)(p + 2);
        var rate = (int)*(uint*)(p + 4);
        var block = *(ushort*)(p + 12);
        var bits = *(ushort*)(p + 14);
        var isFloat = tag == 3 || (tag == 0xFFFE && *(Guid*)(p + 24) == SubtypeFloat);
        if (!(isFloat && bits == 32) && !(!isFloat && bits == 16))
        {
            throw new RecordingException($"不支持的声音格式({bits} 位)");
        }

        Com.Check(initialize(client, 0, flags, 2_000_000, 0, p, 0), "初始化声音设备");
        return new Format(channels, rate, isFloat, block);
    }

    private void Loop(nint capture, Format format)
    {
        var next = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Vtbl(capture)[5];
        var get = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Com.Vtbl(capture)[3];
        var release = (delegate* unmanaged[Stdcall]<nint, uint, int>)Com.Vtbl(capture)[4];
        while (!_stop)
        {
            Thread.Sleep(10);
            uint packet;
            while (!_stop && next(capture, &packet) >= 0 && packet > 0)
            {
                byte* data;
                uint frames, flags;
                ulong position, qpc;
                if (get(capture, &data, &frames, &flags, &position, &qpc) < 0)
                {
                    break;
                }

                float[] stereo;
                if ((flags & BufferSilent) != 0 || data == null)
                {
                    stereo = new float[(int)((long)frames * AudioMixer.SampleRate / format.SampleRate) * 2];
                }
                else
                {
                    var bytes = new ReadOnlySpan<byte>(data, (int)(frames * format.BytesPerFrame));
                    var samples = format.IsFloat ? MemoryMarshal.Cast<byte, float>(bytes).ToArray() : AudioConvert.FromInt16(bytes);
                    stereo = AudioConvert.Resample(AudioConvert.ToStereo(samples, format.Channels), format.SampleRate, AudioMixer.SampleRate);
                }

                _ = release(capture, frames);
                try
                {
                    _onData((long)qpc, stereo);
                }
                catch (Exception)
                {
                    // 写入方出错时停止采集,由录制线程报告
                    _stop = true;
                }
            }
        }
    }
}
