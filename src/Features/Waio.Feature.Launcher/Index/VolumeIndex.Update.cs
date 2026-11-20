using Waio.Feature.Launcher.Infrastructure.Ntfs;

namespace Waio.Feature.Launcher.Index;

/// <summary>按文件号读到的大小 / 修改时间 / 属性(<see cref="NtfsNative.TryGetInfoById"/>)。</summary>
internal readonly record struct FileDetails(long Size, long LastWrite, uint Attributes);

internal sealed partial class VolumeIndex
{
    /// <summary>
    /// 应用一批 USN 日志变化。同一个文件在一批里可能出现很多次(写文件每次追加都有一条),先按文件合并成最后的状态:
    /// 删除 → 从索引去掉;新建 / 改名 → 名称和父目录以最后一条为准;内容或基本信息变了 → 重新读大小和修改时间。
    /// 读大小要开文件(IO),所以在拿写锁之前做;拿锁后改数组,一批通常几毫秒。返回改动了多少条。
    /// </summary>
    public int Apply(IReadOnlyList<UsnChange> changes, Func<long, FileDetails?> details)
    {
        var last = new Dictionary<long, (UsnChange Change, uint Reasons)>();
        foreach (var change in changes)
        {
            var reasons = last.TryGetValue(change.Record, out var seen) ? seen.Reasons | change.Reason : change.Reason;
            if ((change.Reason & UsnChange.FileCreate) != 0 && seen.Change.Name is not null && (seen.Reasons & UsnChange.FileDelete) != 0)
            {
                reasons = change.Reason; // 记录号被删除后又立刻重用:按新文件算
            }

            last[change.Record] = (change, reasons);
        }

        var fetched = new Dictionary<long, FileDetails>();
        foreach (var (record, (change, reasons)) in last)
        {
            if ((reasons & UsnChange.FileDelete) == 0 && (reasons & (UsnChange.FileCreate | UsnChange.DataOverwrite
                | UsnChange.DataExtend | UsnChange.DataTruncation | UsnChange.BasicInfoChange | UsnChange.RenameNewName)) != 0
                && details(change.FileReference) is { } info)
            {
                fetched[record] = info;
            }
        }

        _lock.EnterWriteLock();
        try
        {
            var applied = 0;
            foreach (var (record, (change, reasons)) in last)
            {
                if (record > int.MaxValue - 1)
                {
                    continue;
                }

                var r = (int)record;
                if ((reasons & UsnChange.FileDelete) != 0)
                {
                    if (IsInUse(r) && _seq[r] == change.Sequence)
                    {
                        Remove(r);
                        applied++;
                    }

                    continue;
                }

                var known = IsInUse(r) && _seq[r] == change.Sequence;
                var renamed = (reasons & (UsnChange.FileCreate | UsnChange.RenameNewName)) != 0 || !known;
                if (renamed && (change.Reason & UsnChange.RenameOldName) != 0 && (change.Reason & UsnChange.RenameNewName) == 0)
                {
                    continue; // 最后一条是"改名前的旧名字":新名字那条还没来,下一批再处理
                }

                var visibleBefore = known && IsVisible(r);
                var info = fetched.TryGetValue(record, out var found) ? found : (FileDetails?)null;
                var size = info?.Size ?? (known ? _size[r] : 0);
                var lastWrite = info?.LastWrite ?? (known ? FromUnixSeconds(_mtime[r]) : 0);
                SetRecord(r, change.Sequence, (int)Math.Min(int.MaxValue, change.ParentRecord), change.IsDirectory,
                    info?.Attributes ?? change.Attributes, size, lastWrite);
                if (renamed)
                {
                    SetName(r, change.Name);
                }

                if (_parent[r] >= 0 && _parent[r] < Count)
                {
                    DeriveOne(r);
                }

                var visibleAfter = IsVisible(r);
                Entries += (visibleAfter ? 1 : 0) - (visibleBefore ? 1 : 0);
                applied++;
            }

            if (applied > 0)
            {
                Dirty = true;
            }

            return applied;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 按完整路径找文件夹的记录号(设置里的"排除文件夹"用):在名称块里找最后一段的名称,再核对完整路径。找不到返回 -1。
    /// </summary>
    public int FindFolder(string path)
    {
        var trimmed = path.TrimEnd('\\');
        if (!trimmed.StartsWith(Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (trimmed.Length <= Root.TrimEnd('\\').Length)
        {
            return RootRecord;
        }

        var last = Domain.FoldedText.Encode(Path.GetFileName(trimmed));
        _lock.EnterReadLock();
        try
        {
            var position = 0;
            while (position < _names.Length)
            {
                var hit = _names.Span[position..].IndexOf(last);
                if (hit < 0)
                {
                    return -1;
                }

                var at = position + hit;
                var owner = _names.OwnerAt(at, out var end);
                position = end < 0 ? at + 1 : end + 5;
                if (owner >= 0 && owner < Count && _name[owner] == at && _nameLength[owner] == last.Length
                    && (_flags[owner] & Directory) != 0 && PathOf(owner).Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return owner;
                }
            }

            return -1;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>读锁下取结果要显示的信息(路径、名称、是否文件夹、大小、修改时间);记录已经被删掉返回 null。</summary>
    public FileHitInfo? Describe(int record)
    {
        _lock.EnterReadLock();
        try
        {
            if (!IsInUse(record) || _name[record] < 0)
            {
                return null;
            }

            var path = PathOf(record);
            return path.Length == 0
                ? null
                : new FileHitInfo(path, NameOf(record), (_flags[record] & Directory) != 0, _size[record], ModifiedOf(record));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private static long FromUnixSeconds(uint seconds) => seconds == 0 ? 0 : 116444736000000000 + (seconds * 10_000_000L);
}

internal sealed record FileHitInfo(string Path, string Name, bool IsDirectory, long Size, DateTimeOffset Modified);
