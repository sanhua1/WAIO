using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Waio.Feature.Launcher.Infrastructure.Ntfs;

/// <summary>
/// 读卷、读 USN 日志用到的 Win32 调用和结构(winioctl.h / fileapi.h)。结构按 SDK 头的字段顺序逐个核对过。
/// 打开卷要管理员权限:WAIO 常驻管理员(ADR-0004);拿不到时文件搜索不可用,不影响应用搜索(ADR-0024)。
/// </summary>
internal static class NtfsNative
{
    public const uint FsctlGetNtfsVolumeData = 0x00090064;
    public const uint FsctlQueryUsnJournal = 0x000900F4;
    public const uint FsctlReadUsnJournal = 0x000900BB;
    public const uint FsctlCreateUsnJournal = 0x000900E7;

    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalEntryDeleted = 1181;
    public const int ErrorHandleEof = 38;

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7; // READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UsnJournalData
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    /// <summary>READ_USN_JOURNAL_DATA_V0:不带版本字段时,NTFS 返回 USN_RECORD_V2(64 位文件号)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReadUsnJournalData
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateUsnJournalData
    {
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public int Size;
        public int Type; // 0 = FileIdType(64 位)
        public long FileId;
        public long Padding; // 联合体按 FILE_ID_128 / GUID 的 16 字节
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    /// <summary>打开卷(<c>\\.\C:</c>)只读;失败返回无效句柄,<paramref name="error"/> 是 Win32 错误码。</summary>
    public static SafeFileHandle OpenVolume(string devicePath, out int error)
    {
        var handle = CreateFileW(devicePath, GenericRead, FileShareAll, 0, OpenExisting, 0, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    public static unsafe bool TryGetVolumeData(SafeFileHandle volume, out NtfsVolumeData data)
    {
        data = default;
        fixed (NtfsVolumeData* p = &data)
        {
            return DeviceIoControl(volume, FsctlGetNtfsVolumeData, null, 0, p, (uint)sizeof(NtfsVolumeData), out _, 0);
        }
    }

    /// <summary>查询 USN 日志;没有开启时返回 false(<paramref name="error"/> = 1179)。</summary>
    public static unsafe bool TryQueryJournal(SafeFileHandle volume, out UsnJournalData data, out int error)
    {
        data = default;
        fixed (UsnJournalData* p = &data)
        {
            var ok = DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, p, (uint)sizeof(UsnJournalData), out _, 0);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }

    /// <summary>创建(或调整)USN 日志。Everything 在卷没开日志时也这样做("Enable USN Journal"默认打开)。</summary>
    public static unsafe bool TryCreateJournal(SafeFileHandle volume, ulong maximumSize, ulong allocationDelta)
    {
        var input = new CreateUsnJournalData { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        return DeviceIoControl(volume, FsctlCreateUsnJournal, &input, (uint)sizeof(CreateUsnJournalData), null, 0, out _, 0);
    }

    /// <summary>从 <paramref name="startUsn"/> 读一批日志记录(不等待)。返回写入的字节数;失败返回 -1。</summary>
    public static unsafe int ReadJournal(SafeFileHandle volume, ulong journalId, long startUsn, byte[] buffer, out int error)
    {
        var input = new ReadUsnJournalData { StartUsn = startUsn, ReasonMask = 0xFFFFFFFF, UsnJournalId = journalId };
        fixed (byte* output = buffer)
        {
            if (!DeviceIoControl(volume, FsctlReadUsnJournal, &input, (uint)sizeof(ReadUsnJournalData), output, (uint)buffer.Length, out var returned, 0))
            {
                error = Marshal.GetLastPInvokeError();
                return -1;
            }

            error = 0;
            return (int)returned;
        }
    }

    /// <summary>从卷的 <paramref name="offset"/>(必须按扇区对齐)读 <paramref name="count"/> 字节。返回实际读到的字节数。</summary>
    public static unsafe int ReadAt(SafeFileHandle volume, long offset, Span<byte> buffer)
    {
        if (!SetFilePointerEx(volume, offset, out _, 0))
        {
            return -1;
        }

        fixed (byte* p = buffer)
        {
            return ReadFile(volume, p, (uint)buffer.Length, out var read, 0) ? (int)read : -1;
        }
    }

    /// <summary>
    /// 按文件号取大小和修改时间(USN 日志只告诉"变了",不带大小;同 Everything 的 allow_ntfs_open_file_by_id)。
    /// 文件已经不在了、没有权限返回 false。
    /// </summary>
    public static unsafe bool TryGetInfoById(SafeFileHandle volume, long fileReference, out long size, out long lastWrite, out uint attributes)
    {
        size = 0;
        lastWrite = 0;
        attributes = 0;
        var id = new FileIdDescriptor { Size = sizeof(FileIdDescriptor), Type = 0, FileId = fileReference };
        using var handle = OpenFileById(volume, ref id, FileReadAttributes, FileShareAll, 0, FileFlagBackupSemantics);
        if (handle.IsInvalid)
        {
            return false;
        }

        FileBasicInfo basic;
        FileStandardInfo standard;
        if (!GetFileInformationByHandleEx(handle, 0, &basic, (uint)sizeof(FileBasicInfo))
            || !GetFileInformationByHandleEx(handle, 1, &standard, (uint)sizeof(FileStandardInfo)))
        {
            return false;
        }

        size = standard.Directory != 0 ? 0 : standard.EndOfFile;
        lastWrite = basic.LastWriteTime;
        attributes = basic.FileAttributes;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeviceIoControl(
        SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle file, byte* buffer, uint count, out uint read, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, ref FileIdDescriptor id, uint access, uint share, nint security, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);
}
