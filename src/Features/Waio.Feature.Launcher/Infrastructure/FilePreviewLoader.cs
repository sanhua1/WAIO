using System.Runtime.InteropServices;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Infrastructure;

/// <summary>预览框要显示的文字内容(在后台线程读好,UI 线程只负责显示)。</summary>
/// <param name="TypeName">系统的文件类型名("文本文档""PNG 文件")。</param>
/// <param name="Text">文本预览的内容;不是文本或读不出来为 null。</param>
internal sealed record FilePreviewData(PreviewKind Kind, string TypeName, long Size, string? Text);

/// <summary>读预览用的数据:类型、大小、文本开头。只读,共享打开(别的程序正在写的日志也能看)。</summary>
internal static class FilePreviewLoader
{
    /// <summary>文本最多显示这么多行(TextBlock 太长会卡)。</summary>
    private const int MaxLines = 400;

    public static FilePreviewData Load(string path, bool isDirectory)
    {
        var typeName = TypeName(path, isDirectory);
        if (isDirectory)
        {
            return new FilePreviewData(PreviewKind.None, typeName, -1, null);
        }

        var size = TryLength(path);
        var kind = PreviewRules.KindOf(path);
        if (kind == PreviewKind.Image && size > PreviewRules.MaxImageBytes)
        {
            kind = PreviewKind.None;
        }

        string? text = null;
        if (kind == PreviewKind.Text)
        {
            text = ReadText(path);
            kind = text is null ? PreviewKind.None : PreviewKind.Text;
        }

        return new FilePreviewData(kind, typeName, size, text);
    }

    private static long TryLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[PreviewRules.MaxTextBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return PreviewRules.Decode(buffer.AsSpan(0, read)) is { } text ? FirstLines(text) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FirstLines(string text)
    {
        var index = -1;
        for (var line = 0; line < MaxLines; line++)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0)
            {
                return text;
            }
        }

        return text[..index];
    }

    /// <summary>系统的文件类型名(按扩展名查注册表,不碰磁盘);查不到用"XXX 文件"。</summary>
    private static string TypeName(string path, bool isDirectory)
    {
        var info = default(ShFileInfo);
        var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        if (SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | ShgfiUseFileAttributes) != 0
            && !string.IsNullOrWhiteSpace(info.TypeName))
        {
            return info.TypeName;
        }

        if (isDirectory)
        {
            return "文件夹";
        }

        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length == 0 ? "文件" : $"{extension.ToUpperInvariant()} 文件";
    }

    private const uint ShgfiTypeName = 0x400, ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);
}
