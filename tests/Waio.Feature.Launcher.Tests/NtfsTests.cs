using System.Buffers.Binary;
using System.Text;
using Waio.Feature.Launcher.Index;
using Waio.Feature.Launcher.Infrastructure.Ntfs;

namespace Waio.Feature.Launcher.Tests;

/// <summary>按 NTFS 公开格式拼出 MFT 记录 / USN 记录,测解析和建库(不读真实磁盘)。</summary>
public class NtfsTests
{
    /// <summary>拼一条 1024 字节的 FILE 记录(带 USA 修补),属性按参数给。</summary>
    internal static byte[] Record(
        ushort sequence = 1, bool inUse = true, bool directory = false, long baseRecord = 0,
        (string Name, long Parent, byte Namespace)[]? names = null, long? residentSize = null, long? nonResidentSize = null,
        long lastWrite = 133_000_000_000_000_000, uint attributes = 0x20, bool standardInfo = true)
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 0x30); // USA 偏移
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);    // USA 个数:1 + 2 段
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x10), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x16), (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0x20), (ulong)baseRecord);
        var offset = 0x38;

        void Attribute(uint type, byte[] value, bool nonResident = false, long dataSize = 0)
        {
            var span = record.AsSpan(offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span, type);
            span[9] = 0;
            if (nonResident)
            {
                const int Length = 0x48;
                BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Length);
                span[8] = 1;
                BinaryPrimitives.WriteInt64LittleEndian(span[0x10..], 0); // 最低 VCN
                BinaryPrimitives.WriteUInt16LittleEndian(span[0x20..], 0x40);
                BinaryPrimitives.WriteInt64LittleEndian(span[0x30..], dataSize);
                span[0x40] = 0x11; // 一段:1 字节长度、1 字节偏移
                span[0x41] = 4;
                span[0x42] = 0x10;
                offset += Length;
                return;
            }

            var length = (0x18 + value.Length + 7) & ~7;
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], (uint)value.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x14..], 0x18);
            value.CopyTo(span[0x18..]);
            offset += length;
        }

        if (standardInfo)
        {
            var si = new byte[0x48];
            BinaryPrimitives.WriteInt64LittleEndian(si.AsSpan(0x08), lastWrite);
            BinaryPrimitives.WriteUInt32LittleEndian(si.AsSpan(0x20), attributes);
            Attribute(MftRecord.TypeStandardInformation, si);
        }

        foreach (var (name, parent, ns) in names ?? [])
        {
            var chars = Encoding.Unicode.GetBytes(name);
            var fn = new byte[0x42 + chars.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(fn, (ulong)parent | (1UL << 48));
            fn[0x40] = (byte)name.Length;
            fn[0x41] = ns;
            chars.CopyTo(fn, 0x42);
            Attribute(MftRecord.TypeFileName, fn);
        }

        if (residentSize is { } resident)
        {
            Attribute(MftRecord.TypeData, new byte[resident]);
        }

        if (nonResidentSize is { } large)
        {
            Attribute(MftRecord.TypeData, [], nonResident: true, dataSize: large);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), MftRecord.TypeEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), (uint)(offset + 8));

        // USA:序列号 0x0102,原来每段最后两个字节存进数组,换成序列号
        const ushort Check = 0x0102;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30), Check);
        for (var i = 1; i <= 2; i++)
        {
            var tail = (i * 512) - 2;
            record.AsSpan(tail, 2).CopyTo(record.AsSpan(0x30 + (i * 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(tail), Check);
        }

        return record;
    }

    [Fact]
    public void 记录_修补后解析出名称父目录大小时间()
    {
        var record = Record(sequence: 7, names: [("报告.docx", 40, 1)], nonResidentSize: 123_456, lastWrite: 133_500_000_000_000_000, attributes: 0x22);

        Assert.True(MftRecord.ApplyFixup(record));
        Assert.True(MftRecord.TryParse(record, out var entry));

        Assert.True(entry.InUse);
        Assert.False(entry.IsDirectory);
        Assert.Equal(7, entry.Sequence);
        Assert.Equal("报告.docx", entry.Name.ToString());
        Assert.Equal(40, entry.ParentRecord);
        Assert.Equal(123_456, entry.DataSize);
        Assert.Equal(133_500_000_000_000_000, entry.LastWrite);
        Assert.Equal(0x22u, entry.Attributes);
        Assert.Equal(-1, entry.BaseRecord);
    }

    [Fact]
    public void 记录_序列号对不上_修补失败()
    {
        var record = Record(names: [("a.txt", 5, 1)]);
        record[1022] ^= 0xFF; // 第二段结尾的序列号被改坏(写到一半的记录)

        Assert.False(MftRecord.ApplyFixup(record));
    }

    [Fact]
    public void 记录_只取长名_跳过8点3短名()
    {
        var record = Record(names: [("PROGRA~1", 5, 2), ("Program Files", 5, 1)], directory: true);
        MftRecord.ApplyFixup(record);

        MftRecord.TryParse(record, out var entry);

        Assert.True(entry.IsDirectory);
        Assert.Equal("Program Files", entry.Name.ToString());
    }

    [Fact]
    public void 数据运行_相对偏移可以为负_稀疏段()
    {
        // 段 1:长度 0x10,起始 0x1000;段 2:长度 8,偏移 -0x800;段 3:稀疏 4 簇
        byte[] pairs = [0x21, 0x10, 0x00, 0x10, 0x21, 0x08, 0x00, 0xF8, 0x01, 0x04, 0x00];

        var runs = MftRecord.DecodeRuns(pairs);

        Assert.Equal([(0x1000L, 0x10L), (0x800L, 8L), (-1L, 4L)], runs);
    }

    [Fact]
    public void 建库_扩展记录先于基本记录_名称和大小归到基本记录()
    {
        var index = new VolumeIndex(@"C:\", 1, 64, null);
        var builder = new MftIndexBuilder(index);
        Visit(builder, 5, Record(directory: true, names: [(".", 5, 3)]));
        Visit(builder, 30, Record(baseRecord: 40, standardInfo: false, names: [("大文件.bin", 5, 1)], nonResidentSize: 9_000_000));
        Visit(builder, 40, Record(sequence: 3));
        Visit(builder, 41, Record(inUse: false, names: [("已删除.txt", 5, 1)]));
        index.Derive();

        Assert.Equal(@"C:\大文件.bin", index.PathOf(40));
        Assert.Equal(9_000_000, index.SizeOf(40));
        Assert.False(index.IsInUse(41));
        Assert.Equal(1, index.Entries);
    }

    [Fact]
    public void 日志记录_解析出文件号父目录原因和名称()
    {
        var buffer = new byte[8 + 88];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, 999); // 下一个 USN
        var r = buffer.AsSpan(8);
        var name = Encoding.Unicode.GetBytes("新建文本.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)(60 + name.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(r[4..], 2);
        BinaryPrimitives.WriteInt64LittleEndian(r[8..], (2L << 48) | 77);
        BinaryPrimitives.WriteInt64LittleEndian(r[16..], (1L << 48) | 5);
        BinaryPrimitives.WriteInt64LittleEndian(r[24..], 500);
        BinaryPrimitives.WriteUInt32LittleEndian(r[40..], UsnChange.FileCreate | UsnChange.Close);
        BinaryPrimitives.WriteUInt32LittleEndian(r[52..], 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(r[56..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r[58..], 60);
        name.CopyTo(r[60..]);
        var changes = new List<UsnChange>();

        var next = UsnRecords.Parse(buffer, changes);

        Assert.Equal(999, next);
        var change = Assert.Single(changes);
        Assert.Equal(77, change.Record);
        Assert.Equal(2, change.Sequence);
        Assert.Equal(5, change.ParentRecord);
        Assert.Equal("新建文本.txt", change.Name);
        Assert.True(change.TouchesContent);
        Assert.False(change.IsDirectory);
    }

    private static void Visit(MftIndexBuilder builder, long number, byte[] record)
    {
        Assert.True(MftRecord.ApplyFixup(record));
        builder.Visit(number, record);
    }
}
