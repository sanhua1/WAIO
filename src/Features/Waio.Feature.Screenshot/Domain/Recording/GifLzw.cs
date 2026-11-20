namespace Waio.Feature.Screenshot.Domain.Recording;

/// <summary>
/// GIF 的 LZW 压缩(GIF89a 规范附录 F;ADR-0021)。码表是 8192 格的开放寻址哈希(MAIO 同),满 4096 个码时发清除码重来。
/// 码长增长的时机和解码器严格对齐:每输出一个码之后,如果已分配的码数超过当前码长能表示的最大值,码长加一(经典 compress 的做法)。
/// </summary>
internal static class GifLzw
{
    private const int MaxCode = 4096;
    private const int TableSize = 8192;

    /// <summary>写出:最小码长一个字节 + 数据子块(每块最多 255 字节)+ 块结束符 0。</summary>
    public static void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        output.WriteByte((byte)minCodeSize);
        var writer = new BitWriter(output);
        var keys = new int[TableSize];
        var values = new short[TableSize];
        Array.Fill(keys, -1);

        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var codeSize = minCodeSize + 1;
        var maxCode = (1 << codeSize) - 1;
        var next = clear + 2;
        var clearing = false;

        void Emit(int code)
        {
            writer.Write(code, codeSize);
            if (clearing)
            {
                codeSize = minCodeSize + 1;
                maxCode = (1 << codeSize) - 1;
                clearing = false;
            }
            else if (next > maxCode)
            {
                codeSize++;
                maxCode = codeSize == 12 ? MaxCode : (1 << codeSize) - 1;
            }
        }

        Emit(clear);
        if (indices.IsEmpty)
        {
            Emit(end);
            writer.Finish();
            return;
        }

        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            int c = indices[i];
            var key = (prefix << 8) | c;
            var slot = Slot(key);
            while (keys[slot] != -1 && keys[slot] != key)
            {
                slot = (slot + 1) & (TableSize - 1);
            }

            if (keys[slot] == key)
            {
                prefix = values[slot];
                continue;
            }

            Emit(prefix);
            if (next < MaxCode)
            {
                keys[slot] = key;
                values[slot] = (short)next++;
            }
            else
            {
                Array.Fill(keys, -1);
                next = clear + 2;
                clearing = true;
                Emit(clear);
            }

            prefix = c;
        }

        Emit(prefix);
        Emit(end);
        writer.Finish();
    }

    private static int Slot(int key) => (int)(((uint)key * 2654435761u) >> 19); // 取高 13 位

    /// <summary>低位在前地拼接码字,凑满 255 字节写一个子块。</summary>
    private sealed class BitWriter(Stream output)
    {
        private readonly byte[] _block = new byte[256];
        private int _length;
        private int _bits;
        private int _pending;

        public void Write(int code, int size)
        {
            _pending |= code << _bits;
            _bits += size;
            while (_bits >= 8)
            {
                Put((byte)_pending);
                _pending >>= 8;
                _bits -= 8;
            }
        }

        public void Finish()
        {
            if (_bits > 0)
            {
                Put((byte)_pending);
                _bits = 0;
                _pending = 0;
            }

            Flush();
            output.WriteByte(0);
        }

        private void Put(byte value)
        {
            _block[1 + _length++] = value;
            if (_length == 255)
            {
                Flush();
            }
        }

        private void Flush()
        {
            if (_length == 0)
            {
                return;
            }

            _block[0] = (byte)_length;
            output.Write(_block, 0, _length + 1);
            _length = 0;
        }
    }
}
