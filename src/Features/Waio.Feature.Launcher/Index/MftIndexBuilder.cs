using Waio.Feature.Launcher.Infrastructure.Ntfs;

namespace Waio.Feature.Launcher.Index;

/// <summary>
/// 把读到的每条 MFT 记录放进 <see cref="VolumeIndex"/>。
/// 基本记录:在用标志、目录、属性、修改时间($STANDARD_INFORMATION)、名称和父目录($FILE_NAME)、大小(默认 $DATA)。
/// 扩展记录(属性太多放不下时):里面的名称 / 大小归到它的基本记录;扩展记录可能比基本记录先读到。
/// 纯逻辑(输入是记录字节),可以用拼出来的记录单测。
/// </summary>
internal sealed class MftIndexBuilder(VolumeIndex index)
{
    public VolumeIndex Index => index;

    public long Records { get; private set; }

    public void Visit(long recordNumber, Span<byte> record)
    {
        if (recordNumber > int.MaxValue - 1 || !MftRecord.TryParse(record, out var entry) || !entry.InUse)
        {
            return;
        }

        Records++;
        var number = (int)recordNumber;
        if (entry.BaseRecord >= 0)
        {
            if (entry.BaseRecord > int.MaxValue - 1)
            {
                return;
            }

            var owner = (int)entry.BaseRecord;
            if (entry.HasName && !index.HasName(owner))
            {
                index.SetParent(owner, Parent(entry.ParentRecord));
                index.SetName(owner, entry.Name);
            }

            if (entry.DataSize >= 0)
            {
                index.SetSize(owner, entry.DataSize);
            }

            return;
        }

        var parent = entry.HasName ? Parent(entry.ParentRecord) : index.ParentOf(number);
        var size = entry.DataSize >= 0 ? entry.DataSize : index.PendingSize(number); // 大小可能在先读到的扩展记录里
        index.SetRecord(number, entry.Sequence, parent, entry.IsDirectory, entry.Attributes, size, entry.LastWrite);
        if (entry.HasName)
        {
            index.SetName(number, entry.Name);
        }
    }

    private static int Parent(long record) => record > int.MaxValue - 1 ? 0 : (int)record;
}
