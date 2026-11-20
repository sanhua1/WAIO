using System.Runtime.InteropServices;
using System.Text;

namespace Waio.Feature.Launcher.Infrastructure.Ntfs;

internal enum VolumeKind
{
    Other,
    Fixed,
    Removable,
    Remote,
}

/// <param name="GuidPath"><c>\\?\Volume{…}\</c>。</param>
/// <param name="MountPath">第一个挂载点(<c>C:\</c>);没有挂载点的卷不索引(结果里没法显示路径)。</param>
internal sealed record VolumeInfo(string GuidPath, string MountPath, string FileSystem, VolumeKind Kind, uint Serial)
{
    /// <summary>打开卷用的设备路径(去掉结尾的 \)。</summary>
    public string DevicePath => GuidPath.TrimEnd('\\');

    public bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    public bool IsRefs => FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>列出本机的卷(FindFirstVolume / GetVolumePathNamesForVolumeName / GetDriveType / GetVolumeInformation)。</summary>
internal static class Volumes
{
    public static List<VolumeInfo> List()
    {
        var result = new List<VolumeInfo>();
        var name = new StringBuilder(260);
        var find = FindFirstVolumeW(name, name.Capacity);
        if (find == -1)
        {
            return result;
        }

        try
        {
            do
            {
                var guid = name.ToString();
                var mount = FirstMountPath(guid);
                if (mount is null)
                {
                    continue;
                }

                var fs = new StringBuilder(64);
                var label = new StringBuilder(260);
                if (!GetVolumeInformationW(guid, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity))
                {
                    continue; // 没有介质的读卡器、光驱等
                }

                var kind = GetDriveTypeW(guid) switch
                {
                    3 => VolumeKind.Fixed,
                    2 => VolumeKind.Removable,
                    4 => VolumeKind.Remote,
                    _ => VolumeKind.Other,
                };
                result.Add(new VolumeInfo(guid, mount, fs.ToString(), kind, serial));
            }
            while (FindNextVolumeW(find, name, name.Capacity));
        }
        finally
        {
            FindVolumeClose(find);
        }

        return result.OrderBy(v => v.MountPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FirstMountPath(string guid)
    {
        var buffer = new char[1024];
        if (!GetVolumePathNamesForVolumeNameW(guid, buffer, buffer.Length, out var length) || length <= 1)
        {
            return null;
        }

        var paths = new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return paths.OrderBy(p => p.Length).FirstOrDefault(); // 有盘符的最短
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstVolumeW(StringBuilder name, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(nint find, StringBuilder name, int length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(nint find);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volume, [Out] char[] names, int length, out uint returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string root, StringBuilder label, int labelLength, out uint serial, out uint maxComponent, out uint flags,
        StringBuilder fileSystem, int fileSystemLength);
}
