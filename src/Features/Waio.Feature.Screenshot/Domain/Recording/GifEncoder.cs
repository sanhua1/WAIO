using System.Runtime.InteropServices;

namespace Waio.Feature.Screenshot.Domain.Recording;

/// <summary>
/// 流式 GIF 编码器,边录边编(MAIO 自写的 GIF 编码器思路,ADR-0021;不依赖第三方库):
/// <list type="bullet">
/// <item>每帧只编码和上一帧不同的最小矩形;矩形里没变的像素记为透明,处置方式 1 透出上一帧;</item>
/// <item>每帧自带调色板(<see cref="GifPalette"/>:颜色少时无损,多时中位切分);</item>
/// <item>编码好的一帧先留着,等下一帧到了(知道它显示多久)再写盘,内存里只有上一帧的像素和一帧待写的数据;</item>
/// <item>帧时长按绝对时间取整到 1/100 秒,不累积误差;最短 2/100 秒(更小的值很多播放器当成 1/10 秒)。</item>
/// </list>
/// 不是线程安全的。
/// </summary>
public sealed class GifEncoder : IDisposable
{
    /// <summary>最短帧时长(1/100 秒)。</summary>
    public const int MinDelay = 2;

    private readonly Stream _output;
    private uint[]? _previous;
    private byte[]? _pending; // 编码好、还没写出的一帧(不含图形控制扩展)
    private bool _pendingTransparent;
    private long _pendingStart; // 待写帧开始的时间(1/100 秒,按绝对时间取整)
    private long _written; // 已写出的帧加起来的时长(1/100 秒)
    private bool _finished;

    public GifEncoder(Stream output, int width, int height)
    {
        if (width is <= 0 or > 65535 || height is <= 0 or > 65535)
        {
            throw new ArgumentException($"GIF 尺寸不对:{width}×{height}");
        }

        _output = output;
        Width = width;
        Height = height;
        WriteHeader();
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>写了几帧(和上一帧完全相同的帧不算,它的时间并进上一帧)。</summary>
    public int FrameCount { get; private set; }

    /// <summary>加一帧。<paramref name="bgra"/> 是 宽 × 高 × 4 字节的 BGRA;<paramref name="time"/> 是从录制开始的时间,必须递增。</summary>
    public void AddFrame(ReadOnlySpan<byte> bgra, TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra[..(Width * Height * 4)]);
        var start = Centis(time);
        if (_previous is null)
        {
            _previous = new uint[Width * Height];
            Copy(pixels, _previous);
            Queue(Encode(pixels, new PxRect(0, 0, Width, Height), transparent: false), transparent: false, start);
            return;
        }

        if (DiffRect(pixels, _previous) is not { } rect)
        {
            return; // 完全没变:时间算进上一帧
        }

        var bytes = Encode(pixels, rect, transparent: true);
        Copy(pixels, _previous);
        Queue(bytes, transparent: true, start);
    }

    /// <summary>结束:写出最后一帧(显示到 <paramref name="end"/>)和文件结尾。</summary>
    public void Finish(TimeSpan end)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        WritePending(Centis(end));
        _output.WriteByte(0x3B);
        _output.Flush();
    }

    public void Dispose()
    {
        if (!_finished)
        {
            Finish(TimeSpan.FromMilliseconds(_pendingStart * 10 + 100));
        }
    }

    private static long Centis(TimeSpan time) => (long)Math.Round(time.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);

    private void Queue(byte[] frame, bool transparent, long start)
    {
        WritePending(start);
        _pending = frame;
        _pendingTransparent = transparent;
        _pendingStart = Math.Max(start, _written);
    }

    /// <summary>把待写的帧写出去,显示到 <paramref name="until"/>(1/100 秒)。</summary>
    private void WritePending(long until)
    {
        if (_pending is null)
        {
            return;
        }

        var delay = (int)Math.Clamp(until - _written, MinDelay, 65535);
        if (FrameCount == 0)
        {
            delay = (int)Math.Clamp(until - _pendingStart, MinDelay, 65535);
            _written = _pendingStart;
        }

        _written += delay;

        // 图形控制扩展:处置方式 1(不处置,透出上一帧),透明色下标 255
        Span<byte> control = [0x21, 0xF9, 0x04, (byte)(0x04 | (_pendingTransparent ? 1 : 0)), (byte)delay, (byte)(delay >> 8), 255, 0];
        _output.Write(control);
        _output.Write(_pending);
        _pending = null;
        FrameCount++;
    }

    private void WriteHeader()
    {
        Span<byte> header =
        [
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            (byte)Width, (byte)(Width >> 8), (byte)Height, (byte)(Height >> 8),
            0x70, 0, 0, // 没有全局调色板(每帧自带),色彩分辨率 8 位
        ];
        _output.Write(header);

        // NETSCAPE2.0 扩展:无限循环
        Span<byte> loop =
        [
            0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
            (byte)'2', (byte)'.', (byte)'0', 0x03, 0x01, 0x00, 0x00, 0x00,
        ];
        _output.Write(loop);
    }

    /// <summary>编码矩形 <paramref name="rect"/>:图像描述符 + 局部调色板 + LZW 数据。</summary>
    private byte[] Encode(ReadOnlySpan<uint> pixels, PxRect rect, bool transparent)
    {
        var area = new uint[rect.Width * rect.Height];
        var previous = _previous!;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = (rect.Y + y) * Width + rect.X;
            for (var x = 0; x < rect.Width; x++)
            {
                var p = pixels[row + x] | 0xFF000000u;
                area[y * rect.Width + x] = transparent && (previous[row + x] | 0xFF000000u) == p ? 0u : p; // Alpha 0 = 透明
            }
        }

        var indices = new byte[area.Length];
        var palette = GifPalette.Build(area, transparent ? 255 : 256, indices);

        // 局部调色板大小是 2 的幂(至少 2);有透明时必须包含下标 255,所以直接用 256
        var bits = transparent ? 8 : Math.Max(1, (int)Math.Ceiling(Math.Log2(Math.Max(2, palette.Length))));
        using var stream = new MemoryStream(area.Length / 2 + 1024);
        Span<byte> descriptor =
        [
            0x2C, (byte)rect.X, (byte)(rect.X >> 8), (byte)rect.Y, (byte)(rect.Y >> 8),
            (byte)rect.Width, (byte)(rect.Width >> 8), (byte)rect.Height, (byte)(rect.Height >> 8),
            (byte)(0x80 | (bits - 1)),
        ];
        stream.Write(descriptor);
        var table = new byte[3 << bits];
        for (var i = 0; i < palette.Length; i++)
        {
            table[i * 3] = (byte)(palette[i] >> 16);
            table[i * 3 + 1] = (byte)(palette[i] >> 8);
            table[i * 3 + 2] = (byte)palette[i];
        }

        stream.Write(table);
        GifLzw.Encode(indices, Math.Max(2, bits), stream);
        return stream.ToArray();
    }

    /// <summary>和上一帧不同的像素的外接矩形;完全相同返回 null。</summary>
    private PxRect? DiffRect(ReadOnlySpan<uint> pixels, uint[] previous)
    {
        int top = -1, bottom = -1;
        for (var y = 0; y < Height; y++)
        {
            if (!pixels.Slice(y * Width, Width).SequenceEqual(previous.AsSpan(y * Width, Width)))
            {
                top = top < 0 ? y : top;
                bottom = y;
            }
        }

        if (top < 0)
        {
            return null;
        }

        int left = Width, right = -1;
        for (var y = top; y <= bottom; y++)
        {
            var row = y * Width;
            for (var x = 0; x < left; x++)
            {
                if (pixels[row + x] != previous[row + x])
                {
                    left = x;
                    break;
                }
            }

            for (var x = Width - 1; x > right; x--)
            {
                if (pixels[row + x] != previous[row + x])
                {
                    right = x;
                    break;
                }
            }
        }

        return PxRect.FromEdges(left, top, right + 1, bottom + 1);
    }

    private static void Copy(ReadOnlySpan<uint> source, uint[] target) => source.CopyTo(target);
}
