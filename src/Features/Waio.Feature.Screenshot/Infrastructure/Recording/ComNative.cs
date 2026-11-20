using System.Runtime.InteropServices;

namespace Waio.Feature.Screenshot.Infrastructure.Recording;

/// <summary>录屏失败(设备、编码器、文件)。</summary>
internal sealed class RecordingException(string message, int hresult = 0) : Exception(message)
{
    public int Code { get; } = hresult;
}

/// <summary>
/// 按 vtable 槽位直接调用 COM 接口(录屏用到的 Media Foundation、WASAPI 接口都只用少数几个方法,ADR-0021)。
/// 槽位都对照 Windows SDK 头文件(mfobjects.h、mfreadwrite.h、mmdeviceapi.h、audioclient.h)核对过;IUnknown 占 0~2。
/// </summary>
internal static unsafe class Com
{
    public static void** Vtbl(nint obj) => *(void***)obj;

    public static void Release(ref nint obj)
    {
        if (obj != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(obj)[2])(obj);
            obj = 0;
        }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new RecordingException($"{what}失败(0x{hr:X8})", hr);
        }
    }

    /// <summary>当前线程加入多线程套间(已经初始化过就什么都不做)。Media Foundation、WASAPI 都要求先初始化 COM。</summary>
    public static void EnsureMta() => _ = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(nint pointer);
}

/// <summary>Media Foundation 的函数、GUID 和接口槽位(mfapi.h、mfobjects.h、mfreadwrite.h)。</summary>
internal static unsafe class Mf
{
    public const uint Version = 0x00020070; // MF_VERSION

    // IMFAttributes(IMFMediaType、IMFSample 都继承它)
    private const int SetUint32Slot = 21, SetUint64Slot = 22, SetGuidSlot = 24;

    // IMFSample(IMFAttributes 之后)
    private const int SetSampleTimeSlot = 36, SetSampleDurationSlot = 38, AddBufferSlot = 42;

    // IMFMediaBuffer
    private const int LockSlot = 3, UnlockSlot = 4, SetCurrentLengthSlot = 6;

    // IMFSinkWriter
    private const int AddStreamSlot = 3, SetInputMediaTypeSlot = 4, BeginWritingSlot = 5, WriteSampleSlot = 6, FinalizeSlot = 11;

    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioAac = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid AudioPcm = new("00000001-0000-0010-8000-00AA00389B71");

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid AudioSampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid AudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");

    [DllImport("mfplat.dll")]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out nint mediaType);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateAttributes(out nint attributes, uint initialSize);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out nint sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, nint byteStream, nint attributes, out nint writer);

    public static int SetUint32(nint attributes, in Guid key, uint value)
    {
        fixed (Guid* k = &key)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.Vtbl(attributes)[SetUint32Slot])(attributes, k, value);
        }
    }

    public static int SetUint64(nint attributes, in Guid key, ulong value)
    {
        fixed (Guid* k = &key)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Com.Vtbl(attributes)[SetUint64Slot])(attributes, k, value);
        }
    }

    public static int SetGuid(nint attributes, in Guid key, in Guid value)
    {
        fixed (Guid* k = &key)
        fixed (Guid* v = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Vtbl(attributes)[SetGuidSlot])(attributes, k, v);
        }
    }

    /// <summary>两个 32 位数拼成一个 64 位属性(帧大小、帧率、像素宽高比)。</summary>
    public static ulong Pack(uint high, uint low) => (ulong)high << 32 | low;

    public static int SetSampleTime(nint sample, long time) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Com.Vtbl(sample)[SetSampleTimeSlot])(sample, time);

    public static int SetSampleDuration(nint sample, long duration) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Com.Vtbl(sample)[SetSampleDurationSlot])(sample, duration);

    public static int AddBuffer(nint sample, nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.Vtbl(sample)[AddBufferSlot])(sample, buffer);

    public static int Lock(nint buffer, out byte* data)
    {
        byte* pointer;
        var hr = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Com.Vtbl(buffer)[LockSlot])(buffer, &pointer, null, null);
        data = pointer;
        return hr;
    }

    public static int Unlock(nint buffer) => ((delegate* unmanaged[Stdcall]<nint, int>)Com.Vtbl(buffer)[UnlockSlot])(buffer);

    public static int SetCurrentLength(nint buffer, uint length) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Com.Vtbl(buffer)[SetCurrentLengthSlot])(buffer, length);

    public static int AddStream(nint writer, nint mediaType, out uint stream)
    {
        uint index;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Com.Vtbl(writer)[AddStreamSlot])(writer, mediaType, &index);
        stream = index;
        return hr;
    }

    public static int SetInputMediaType(nint writer, uint stream, nint mediaType) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Com.Vtbl(writer)[SetInputMediaTypeSlot])(writer, stream, mediaType, 0);

    public static int BeginWriting(nint writer) => ((delegate* unmanaged[Stdcall]<nint, int>)Com.Vtbl(writer)[BeginWritingSlot])(writer);

    public static int WriteSample(nint writer, uint stream, nint sample) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Com.Vtbl(writer)[WriteSampleSlot])(writer, stream, sample);

    public static int Finalize(nint writer) => ((delegate* unmanaged[Stdcall]<nint, int>)Com.Vtbl(writer)[FinalizeSlot])(writer);

    // IMFSourceReader(mfreadwrite.h)
    private const int SetStreamSelectionSlot = 4, GetCurrentMediaTypeSlot = 6, ReadSampleSlot = 9;

    /// <summary>MF_SOURCE_READER_ALL_STREAMS / ANY_STREAM。</summary>
    public const uint AllStreams = 0xFFFFFFFE;

    /// <summary>MF_SOURCE_READERF_ERROR / ENDOFSTREAM。</summary>
    public const uint ReaderError = 0x1, ReaderEndOfStream = 0x2;

    /// <summary>MF_TRANSCODE_CONTAINERTYPE:Sink Writer 用哪种容器(不设时按扩展名)。</summary>
    public static readonly Guid TranscodeContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");

    /// <summary>MFTranscodeContainerType_FMPEG4:分段 MP4,写到一半进程没了,已经写下的部分也能播放。</summary>
    public static readonly Guid ContainerFragmentedMp4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004");

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);

    public static int SetStreamSelection(nint reader, uint stream, bool selected) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Vtbl(reader)[SetStreamSelectionSlot])(reader, stream, selected ? 1 : 0);

    /// <summary>当前输出格式(没改过就是文件里的原始压缩格式,用来原样转封装)。超出流的个数时返回 MF_E_INVALIDSTREAMNUMBER。</summary>
    public static int GetCurrentMediaType(nint reader, uint stream, out nint mediaType)
    {
        nint type;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.Vtbl(reader)[GetCurrentMediaTypeSlot])(reader, stream, &type);
        mediaType = type;
        return hr;
    }

    /// <summary>同步读下一个样本(任意已选中的流)。<paramref name="sample"/> 可能为 0(流结束、间隔)。</summary>
    public static int ReadSample(nint reader, out uint actualStream, out uint flags, out long time, out nint sample)
    {
        uint index, streamFlags;
        long timestamp;
        nint result;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Com.Vtbl(reader)[ReadSampleSlot])(
            reader, AllStreams, 0, &index, &streamFlags, &timestamp, &result);
        actualStream = index;
        flags = streamFlags;
        time = timestamp;
        sample = result;
        return hr;
    }
}
