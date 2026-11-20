using System.Buffers.Binary;

namespace Waio.Feature.Launcher.Infrastructure.Ntfs;

/// <summary>一条 MFT 记录里索引需要的东西。名称是 UTF-16,指向记录缓冲区内部(调用方在缓冲区失效前用掉)。</summary>
internal ref struct MftEntry
{
    public bool InUse;
    public bool IsDirectory;

    /// <summary>扩展记录指向的基本记录号;本身就是基本记录时为 -1。</summary>
    public long BaseRecord;
    public ushort Sequence;

    public bool HasStandardInfo;
    public long LastWrite;
    public uint Attributes;

    public bool HasName;
    public long ParentRecord;
    public ushort ParentSequence;
    public ReadOnlySpan<char> Name;

    /// <summary>默认数据流的大小;只有第一段(最低 VCN 为 0)才有,否则为 -1。</summary>
    public long DataSize;

    /// <summary>有属性列表($ATTRIBUTE_LIST):部分属性在扩展记录里。</summary>
    public bool HasAttributeList;
}

/// <summary>
/// MFT 记录(FILE 记录段)的解析,按 NTFS 的公开格式(FILE_RECORD_SEGMENT_HEADER / ATTRIBUTE_RECORD_HEADER / FILE_NAME)。
/// 代码按格式自己写。纯逻辑,可单测。
/// </summary>
internal static class MftRecord
{
    public const uint TypeStandardInformation = 0x10;
    public const uint TypeAttributeList = 0x20;
    public const uint TypeFileName = 0x30;
    public const uint TypeData = 0x80;
    public const uint TypeEnd = 0xFFFFFFFF;

    /// <summary>更新序列数组(USA)的步长:NTFS 固定按 512 字节一段保护。</summary>
    public const int UsaStride = 512;

    private const byte NamespaceDos = 2;

    /// <summary>
    /// 应用更新序列修补:每 512 字节最后两个字节存的是序列号,真实值在 USA 里。
    /// 序列号对不上(写到一半的记录)返回 false。
    /// </summary>
    public static bool ApplyFixup(Span<byte> record)
    {
        if (record.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x454C4946) // "FILE"
        {
            return false;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 1 || usaOffset + (usaCount * 2) > record.Length || (usaCount - 1) * UsaStride > record.Length)
        {
            return false;
        }

        var check = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var tail = (i * UsaStride) - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[tail..]) != check)
            {
                return false;
            }

            record[(usaOffset + (i * 2))..(usaOffset + (i * 2) + 2)].CopyTo(record[tail..]);
        }

        return true;
    }

    /// <summary>解析已经修补过的记录。格式不对返回 false。</summary>
    public static bool TryParse(ReadOnlySpan<byte> record, out MftEntry entry)
    {
        entry = default;
        entry.BaseRecord = -1;
        entry.DataSize = -1;
        if (record.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x454C4946)
        {
            return false;
        }

        entry.Sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
        int attributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        var bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
        entry.InUse = (flags & 0x1) != 0;
        entry.IsDirectory = (flags & 0x2) != 0;
        if ((baseReference & 0xFFFF_FFFF_FFFF) != 0)
        {
            entry.BaseRecord = (long)(baseReference & 0xFFFF_FFFF_FFFF);
        }

        var bestNamespace = -1;
        var offset = attributeOffset;
        while (offset + 16 <= bytesInUse)
        {
            var attribute = record[offset..bytesInUse];
            var type = BinaryPrimitives.ReadUInt32LittleEndian(attribute);
            if (type == TypeEnd)
            {
                break;
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[4..]);
            if (length < 16 || length > attribute.Length)
            {
                return false; // 长度不对:记录损坏
            }

            attribute = attribute[..length];
            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];
            switch (type)
            {
                case TypeStandardInformation when !nonResident && Resident(attribute) is { Length: >= 0x24 } value:
                    entry.HasStandardInfo = true;
                    entry.LastWrite = BinaryPrimitives.ReadInt64LittleEndian(value[0x08..]);
                    entry.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(value[0x20..]);
                    break;
                case TypeAttributeList:
                    entry.HasAttributeList = true;
                    break;
                case TypeFileName when !nonResident && Resident(attribute) is { Length: >= 0x42 } value:
                    var ns = value[0x41];
                    var chars = value[0x40];
                    if (ns == NamespaceDos || 0x42 + (chars * 2) > value.Length)
                    {
                        break; // 只有 8.3 短名的那份不要(同一个文件还有一份长名)
                    }

                    // 同一个记录有多个名称(硬链接)时取第一个;Win32 名优先于 POSIX
                    var rank = ns == 0 ? 1 : 2;
                    if (rank > bestNamespace)
                    {
                        bestNamespace = rank;
                        var parent = BinaryPrimitives.ReadUInt64LittleEndian(value);
                        entry.HasName = true;
                        entry.ParentRecord = (long)(parent & 0xFFFF_FFFF_FFFF);
                        entry.ParentSequence = (ushort)(parent >> 48);
                        entry.Name = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(value.Slice(0x42, chars * 2));
                    }

                    break;
                case TypeData when nameLength == 0:
                    if (!nonResident)
                    {
                        entry.DataSize = BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
                    }
                    else if (attribute.Length >= 0x40 && BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]) == 0)
                    {
                        entry.DataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
                    }

                    break;
            }

            offset += length;
        }

        return true;
    }

    /// <summary>非常驻属性的数据运行(mapping pairs)所在的那段字节;常驻属性返回空。</summary>
    public static ReadOnlySpan<byte> MappingPairs(ReadOnlySpan<byte> attribute)
    {
        if (attribute.Length < 0x40 || attribute[8] == 0)
        {
            return [];
        }

        int pairs = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
        return pairs < attribute.Length ? attribute[pairs..] : [];
    }

    /// <summary>
    /// 解码数据运行:每段 = 头字节(低 4 位 = 长度占几个字节,高 4 位 = 起始簇偏移占几个字节)+ 长度 + 相对上一段的有符号簇偏移。
    /// 偏移占 0 个字节的是稀疏段(<c>Lcn = -1</c>)。
    /// </summary>
    public static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> pairs)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        var i = 0;
        while (i < pairs.Length && pairs[i] != 0)
        {
            var header = pairs[i++];
            int lengthBytes = header & 0xF;
            int offsetBytes = header >> 4;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || i + lengthBytes + offsetBytes > pairs.Length)
            {
                break;
            }

            long clusters = 0;
            for (var b = 0; b < lengthBytes; b++)
            {
                clusters |= (long)pairs[i + b] << (8 * b);
            }

            i += lengthBytes;
            if (offsetBytes == 0)
            {
                runs.Add((-1, clusters));
                continue;
            }

            long delta = 0;
            for (var b = 0; b < offsetBytes; b++)
            {
                delta |= (long)pairs[i + b] << (8 * b);
            }

            if ((pairs[i + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8)
            {
                delta |= -1L << (8 * offsetBytes); // 符号扩展
            }

            i += offsetBytes;
            lcn += delta;
            runs.Add((lcn, clusters));
        }

        return runs;
    }

    /// <summary>找第一个指定类型、名字为空的属性(整段属性字节);没有返回空。</summary>
    public static ReadOnlySpan<byte> FindAttribute(ReadOnlySpan<byte> record, uint wanted)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        var bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        while (offset + 16 <= bytesInUse)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (type == TypeEnd || length < 16 || offset + length > bytesInUse)
            {
                break;
            }

            if (type == wanted && record[offset + 9] == 0)
            {
                return record.Slice(offset, length);
            }

            offset += length;
        }

        return [];
    }

    private static ReadOnlySpan<byte> Resident(ReadOnlySpan<byte> attribute)
    {
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        int start = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        return start + length <= attribute.Length ? attribute.Slice(start, length) : [];
    }
}
