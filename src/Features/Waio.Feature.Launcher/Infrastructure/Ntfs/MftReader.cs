using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Waio.Feature.Launcher.Infrastructure.Ntfs;

/// <param name="record">已经做过 USA 修补的记录;只在回调期间有效。</param>
internal delegate void MftRecordVisitor(long recordNumber, Span<byte> record);

/// <summary>
/// 把一个 NTFS 卷的 $MFT 从头读到尾(Everything 的做法:直接读卷上的 $MFT,而不是逐个目录列举,几百万条只要几秒)。
/// 步骤:FSCTL_GET_NTFS_VOLUME_DATA 拿到 $MFT 起始簇和记录大小 → 读 0 号记录($MFT 自己)→ 解码它的 $DATA 数据运行
/// (MFT 碎片多时一部分在扩展记录里,经 $ATTRIBUTE_LIST 找到)→ 按运行大块顺序读,只读到 MftValidDataLength。
/// 需要管理员权限打开卷。在后台线程调用,不碰界面。
/// </summary>
internal sealed class MftReader(SafeFileHandle volume, NtfsNative.NtfsVolumeData data)
{
    private const int ChunkBytes = 8 << 20;

    public int RecordSize => (int)data.BytesPerFileRecordSegment;

    /// <summary>有效部分里一共多少条记录(包括没用到的)。</summary>
    public long RecordCount => data.MftValidDataLength / data.BytesPerFileRecordSegment;

    /// <summary>
    /// 逐条读,修补后交给 <paramref name="visit"/>(修补失败、不是 FILE 记录的跳过)。
    /// <paramref name="progress"/> 每读一块报告一次已读的记录数。
    /// </summary>
    public void ReadAll(MftRecordVisitor visit, Action<long>? progress = null, CancellationToken cancellation = default)
    {
        var runs = MftRuns();
        var cluster = (long)data.BytesPerCluster;
        var recordSize = RecordSize;
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes);
        long vcnBytes = 0; // 已经读过的 $MFT 字节数(= 下一条记录的位置)
        var limit = data.MftValidDataLength;
        foreach (var (lcn, clusters) in runs)
        {
            var runBytes = clusters * cluster;
            long done = 0;
            while (done < runBytes && vcnBytes < limit)
            {
                cancellation.ThrowIfCancellationRequested();
                var want = (int)Math.Min(Math.Min(ChunkBytes, runBytes - done), RoundUp(limit - vcnBytes, cluster));
                want -= want % recordSize;
                if (want <= 0)
                {
                    break;
                }

                var span = buffer.AsSpan(0, want);
                if (lcn < 0)
                {
                    span.Clear(); // 稀疏段:没有记录
                }
                else if (NtfsNative.ReadAt(volume, (lcn * cluster) + done, span) != want)
                {
                    throw new IOException($"读 $MFT 失败(簇 {lcn},偏移 {done})");
                }

                for (var at = 0; at + recordSize <= want; at += recordSize)
                {
                    var number = (vcnBytes + at) / recordSize;
                    if (vcnBytes + at >= limit)
                    {
                        break;
                    }

                    var record = span.Slice(at, recordSize);
                    if (MftRecord.ApplyFixup(record))
                    {
                        visit(number, record);
                    }
                }

                done += want;
                vcnBytes += want;
                progress?.Invoke(vcnBytes / recordSize);
            }

            if (vcnBytes >= limit)
            {
                break;
            }
        }
    }

    /// <summary>$MFT 自己的数据运行(按 VCN 顺序)。</summary>
    internal List<(long Lcn, long Clusters)> MftRuns()
    {
        var cluster = (long)data.BytesPerCluster;
        var record0 = ReadRecordAt(data.MftStartLcn * cluster);
        var dataAttribute = MftRecord.FindAttribute(record0, MftRecord.TypeData);
        var segments = new List<(long Vcn, List<(long, long)> Runs)>();
        if (dataAttribute.Length > 0)
        {
            segments.Add((LowestVcn(dataAttribute), MftRecord.DecodeRuns(MftRecord.MappingPairs(dataAttribute))));
        }

        // MFT 碎片太多时,$DATA 的后续段放在扩展记录里,0 号记录只有 $ATTRIBUTE_LIST 指向它们
        var list = MftRecord.FindAttribute(record0, MftRecord.TypeAttributeList);
        if (list.Length > 0 && list[8] == 0)
        {
            var firstRuns = segments.Count > 0 ? segments[0].Runs : [];
            foreach (var (vcn, recordNumber) in DataSegmentsInList(list))
            {
                if (recordNumber == 0 || segments.Exists(s => s.Vcn == vcn))
                {
                    continue;
                }

                var location = Locate(firstRuns, recordNumber);
                if (location < 0)
                {
                    continue;
                }

                var extension = ReadRecordAt(location);
                var segment = MftRecord.FindAttribute(extension, MftRecord.TypeData);
                if (segment.Length > 0)
                {
                    segments.Add((LowestVcn(segment), MftRecord.DecodeRuns(MftRecord.MappingPairs(segment))));
                }
            }
        }

        if (segments.Count == 0)
        {
            throw new IOException("$MFT 没有数据运行");
        }

        return segments.OrderBy(s => s.Vcn).SelectMany(s => s.Runs).ToList();
    }

    private byte[] ReadRecordAt(long offset)
    {
        var sector = Math.Max(512, (int)data.BytesPerSector);
        var size = (int)RoundUp(Math.Max(RecordSize, sector), sector);
        var aligned = offset - (offset % sector);
        var buffer = new byte[size + sector];
        if (NtfsNative.ReadAt(volume, aligned, buffer) <= 0)
        {
            throw new IOException("读 $MFT 记录失败");
        }

        var record = buffer.AsSpan((int)(offset - aligned), RecordSize).ToArray();
        if (!MftRecord.ApplyFixup(record))
        {
            throw new IOException("$MFT 记录损坏");
        }

        return record;
    }

    /// <summary>记录号 → 卷上的字节位置(只用第一段运行,扩展记录一般都在里面)。找不到返回 -1。</summary>
    private long Locate(List<(long Lcn, long Clusters)> runs, long recordNumber)
    {
        var cluster = (long)data.BytesPerCluster;
        var position = recordNumber * RecordSize;
        foreach (var (lcn, clusters) in runs)
        {
            var bytes = clusters * cluster;
            if (position < bytes)
            {
                return lcn < 0 ? -1 : (lcn * cluster) + position;
            }

            position -= bytes;
        }

        return -1;
    }

    /// <summary>常驻的属性列表里,类型为 $DATA、名字为空的各项:(起始 VCN, 所在记录号)。</summary>
    private static IEnumerable<(long Vcn, long Record)> DataSegmentsInList(ReadOnlySpan<byte> attribute)
    {
        var result = new List<(long, long)>();
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        int start = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        var value = start + length <= attribute.Length ? attribute.Slice(start, length) : [];
        var offset = 0;
        while (offset + 0x1A <= value.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);
            int entryLength = BinaryPrimitives.ReadUInt16LittleEndian(value[(offset + 4)..]);
            if (entryLength < 0x1A)
            {
                break;
            }

            if (type == MftRecord.TypeData && value[offset + 6] == 0)
            {
                var vcn = BinaryPrimitives.ReadInt64LittleEndian(value[(offset + 8)..]);
                var reference = BinaryPrimitives.ReadUInt64LittleEndian(value[(offset + 0x10)..]);
                result.Add((vcn, (long)(reference & 0xFFFF_FFFF_FFFF)));
            }

            offset += entryLength;
        }

        return result;
    }

    private static long LowestVcn(ReadOnlySpan<byte> attribute) =>
        attribute[8] != 0 && attribute.Length >= 0x18 ? BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]) : 0;

    private static long RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;
}
