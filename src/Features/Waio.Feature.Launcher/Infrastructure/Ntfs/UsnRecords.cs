using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Waio.Feature.Launcher.Infrastructure.Ntfs;

/// <summary>USN 日志里的一条变化(USN_RECORD_V2)。文件号低 48 位是记录号,高 16 位是序列号。</summary>
internal readonly record struct UsnChange(long FileReference, long ParentReference, long Usn, uint Reason, uint Attributes, string Name)
{
    public const uint DataOverwrite = 0x1;
    public const uint DataExtend = 0x2;
    public const uint DataTruncation = 0x4;
    public const uint FileCreate = 0x100;
    public const uint FileDelete = 0x200;
    public const uint RenameOldName = 0x1000;
    public const uint RenameNewName = 0x2000;
    public const uint BasicInfoChange = 0x8000;
    public const uint Close = 0x80000000;

    public const uint DirectoryAttribute = 0x10;

    public long Record => FileReference & 0xFFFF_FFFF_FFFF;

    public ushort Sequence => (ushort)((ulong)FileReference >> 48);

    public long ParentRecord => ParentReference & 0xFFFF_FFFF_FFFF;

    public bool IsDirectory => (Attributes & DirectoryAttribute) != 0;

    /// <summary>大小、修改时间可能变了(要重新读一次)。</summary>
    public bool TouchesContent => (Reason & (DataOverwrite | DataExtend | DataTruncation | BasicInfoChange | FileCreate)) != 0;
}

/// <summary>
/// 解析 FSCTL_READ_USN_JOURNAL 的输出:开头 8 字节是下一次该从哪个 USN 读,后面是一条条 USN_RECORD_V2。
/// 纯逻辑,可单测。
/// </summary>
internal static class UsnRecords
{
    public static long Parse(ReadOnlySpan<byte> output, List<UsnChange> changes)
    {
        if (output.Length < 8)
        {
            return -1;
        }

        var next = BinaryPrimitives.ReadInt64LittleEndian(output);
        var offset = 8;
        while (offset + 60 <= output.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(output[offset..]);
            if (length < 60 || offset + length > output.Length)
            {
                break;
            }

            var record = output.Slice(offset, length);
            var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            if (major == 2)
            {
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
                int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
                if (nameOffset + nameLength <= length)
                {
                    var name = new string(MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength)));
                    changes.Add(new UsnChange(
                        BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
                        BinaryPrimitives.ReadInt64LittleEndian(record[16..]),
                        BinaryPrimitives.ReadInt64LittleEndian(record[24..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(record[52..]),
                        name));
                }
            }

            offset += (length + 7) & ~7; // 记录按 8 字节对齐
        }

        return next;
    }
}
