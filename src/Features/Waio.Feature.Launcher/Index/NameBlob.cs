using System.Buffers.Binary;

namespace Waio.Feature.Launcher.Index;

/// <summary>
/// 名称块:所有名称首尾相接存成一整块字节,每个名称后面跟 <c>0</c> 和 4 字节的"属于哪个记录"。
/// 搜索时直接在整块上用 SIMD 的 <c>IndexOf</c> 找,命中后往后找到 0 就知道是谁的(做法同 MAIO / oil-find 的名称块;
/// 把记录号写在名称后面是这里的做法,省掉一张"位置 → 记录"的表)。
/// 改名时新名称追加到末尾,旧的那段成为"死"字节,存盘前压缩(<see cref="VolumeIndex.Compact"/>)。
/// </summary>
internal sealed class NameBlob
{
    private byte[] _data;

    public NameBlob(int capacity = 1 << 16) => _data = new byte[Math.Max(16, capacity)];

    internal NameBlob(byte[] data, int length)
    {
        _data = data;
        Length = length;
    }

    public int Length { get; private set; }

    /// <summary>已经没人用的字节数(改名、删除留下的)。</summary>
    public long Dead { get; set; }

    public ReadOnlySpan<byte> Span => _data.AsSpan(0, Length);

    internal byte[] Raw => _data;

    /// <summary>追加一个名称,返回它的起始位置。</summary>
    public int Append(ReadOnlySpan<byte> name, int owner)
    {
        var needed = Length + name.Length + 5;
        if (needed > _data.Length)
        {
            var grown = new byte[Math.Max(needed, (int)Math.Min(Array.MaxLength, (long)_data.Length * 3 / 2))];
            _data.AsSpan(0, Length).CopyTo(grown);
            _data = grown;
        }

        var start = Length;
        name.CopyTo(_data.AsSpan(start));
        _data[start + name.Length] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(start + name.Length + 1), owner);
        Length = needed;
        return start;
    }

    /// <summary>
    /// <paramref name="position"/> 处的命中属于谁:往后找到名称结尾的 0,读出后面的记录号。
    /// 调用方还要用记录自己的名称位置核对一次(命中可能落在记录号那 4 个字节上)。找不到返回 -1。
    /// </summary>
    public int OwnerAt(int position, out int end)
    {
        end = -1;
        var rest = Span[position..];
        var zero = rest.Length > 1100 ? rest[..1100].IndexOf((byte)0) : rest.IndexOf((byte)0);
        if (zero < 0 || position + zero + 5 > Length)
        {
            return -1;
        }

        end = position + zero;
        return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(end + 1));
    }

    public ReadOnlySpan<byte> At(int start, int length) => _data.AsSpan(start, length);

    /// <summary>容量收紧到实际长度(+16 字节余量,搜索时越界检查用不到)的副本;本来就紧的返回自己。</summary>
    public NameBlob Trimmed()
    {
        if (_data.Length - Length < 4096)
        {
            return this;
        }

        var data = GC.AllocateUninitializedArray<byte>(Length + 16);
        _data.AsSpan(0, Length).CopyTo(data);
        return new NameBlob(data, Length) { Dead = Dead };
    }
}
