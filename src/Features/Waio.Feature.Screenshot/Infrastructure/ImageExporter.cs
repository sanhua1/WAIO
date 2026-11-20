using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 保存截图文件(MAIO ImageExporter 的保存部分)。保存位置是用户指定的导出位置,不属于功能数据目录(ADR-0015 说明了这个例外)。
/// </summary>
internal static class ImageExporter
{
    /// <summary>默认保存文件夹:"图片\WAIO"(MAIO 是 ~/Pictures/MAIO)。</summary>
    public static string DefaultDirectory()
    {
        try
        {
            return Path.Combine(Windows.Storage.UserDataPaths.GetDefault().Pictures, "WAIO");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return Path.Combine(Windows.Storage.UserDataPaths.GetDefault().Profile, "Pictures", "WAIO");
        }
    }

    /// <summary>默认录屏文件夹:"视频\WAIO"(MAIO 是 ~/Movies/MAIO)。</summary>
    public static string DefaultRecordingDirectory()
    {
        try
        {
            return Path.Combine(Windows.Storage.UserDataPaths.GetDefault().Videos, "WAIO");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return Path.Combine(Windows.Storage.UserDataPaths.GetDefault().Profile, "Videos", "WAIO");
        }
    }

    public static string RecordingDirectory(string configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultRecordingDirectory() : Environment.ExpandEnvironmentVariables(configured.Trim());

    public static string Directory(string configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultDirectory() : Environment.ExpandEnvironmentVariables(configured.Trim());

    /// <summary>写到文件夹里,文件名按时间生成(重名加序号)。返回文件完整路径。</summary>
    public static string Save(byte[] png, string directory, DateTimeOffset date)
    {
        System.IO.Directory.CreateDirectory(directory);
        var name = ScreenshotFileName.Make(date.ToLocalTime(), n => File.Exists(Path.Combine(directory, n)));
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>
    /// "另存为"对话框(MAIO askSaveLocation 的 NSSavePanel)。用经典的 <c>GetSaveFileNameW</c>:
    /// WinRT 的 FileSavePicker 在管理员进程里用不了(WAIO 常驻管理员,ADR-0004)。用户取消返回 null。
    /// </summary>
    public static string? AskSaveLocation(nint owner, string directory, DateTimeOffset date)
    {
        System.IO.Directory.CreateDirectory(directory);
        var suggested = ScreenshotFileName.Make(date.ToLocalTime(), n => File.Exists(Path.Combine(directory, n)));
        var buffer = new char[1024];
        suggested.CopyTo(0, buffer, 0, suggested.Length);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                Filter = "PNG 图片 (*.png)\0*.png\0\0",
                File = handle.AddrOfPinnedObject(),
                MaxFile = buffer.Length,
                InitialDir = directory,
                Title = "保存截图",
                DefExt = "png",
                Flags = 0x2 | 0x800 | 0x80000, // OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_EXPLORER
            };
            if (!GetSaveFileNameW(ref dialog))
            {
                return null;
            }

            return new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public string Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public string InitialDir;
        public string Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string DefExt;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
        public nint Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OpenFileName dialog);
}
