using System.Runtime.InteropServices;

namespace Waio.Shell.Settings;

/// <summary>
/// "浏览…"用的文件选择对话框。用 Win32 的 <c>GetOpenFileNameW</c> 而不是 WinRT 的 <c>FileOpenPicker</c>:
/// WAIO 常驻管理员(ADR-0004),WinRT 文件选择器在提权进程里不能用(直接抛异常),经典对话框没有这个限制。
/// </summary>
internal static class FileDialogs
{
    private const int OfnFileMustExist = 0x1000, OfnPathMustExist = 0x800, OfnNoChangeDir = 0x8, OfnExplorer = 0x80000;
    private const int OfnNoDereferenceLinks = 0x100000;
    private const int BufferChars = 4096;

    /// <summary>选一个文件;用户取消返回 null。会阻塞 UI 线程直到对话框关闭(对话框自己转消息循环)。</summary>
    /// <param name="owner">父窗口句柄,对话框模态于它。</param>
    public static string? PickFile(nint owner, string title, string? initialPath = null)
    {
        var buffer = Marshal.AllocHGlobal(BufferChars * sizeof(char));
        try
        {
            var initial = File.Exists(initialPath) ? Path.GetFileName(initialPath) : "";
            var chars = initial.ToCharArray();
            Marshal.Copy(chars, 0, buffer, Math.Min(chars.Length, BufferChars - 1));
            Marshal.WriteInt16(buffer, Math.Min(chars.Length, BufferChars - 1) * sizeof(char), 0);

            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                // 每对"说明\0通配符\0",以额外的 \0 结束(封送时会再补一个)
                Filter = "程序和快捷方式\0*.exe;*.lnk;*.bat;*.cmd;*.msc\0所有文件\0*.*\0",
                File = buffer,
                MaxFile = BufferChars,
                InitialDir = File.Exists(initialPath) ? Path.GetDirectoryName(initialPath) : null,
                Title = title,
                // 选 .lnk 时返回快捷方式本身而不是它指向的目标:启动快捷方式能保留其中的参数和工作目录
                Flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnExplorer | OfnNoDereferenceLinks,
            };

            return GetOpenFileNameW(ref dialog) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public string? Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefExt;
        public nint CustomData;
        public nint Hook;
        public string? TemplateName;
        public nint Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }
}
