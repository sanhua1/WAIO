using System.Runtime.InteropServices;
using System.Text;
using Waio.Kit.Services;
using static Waio.Core.Clipboard.ClipboardNative;

namespace Waio.Core.Clipboard;

/// <summary>
/// 剪贴板各格式与 <see cref="ClipboardContent"/> 之间的转换。<b>调用方必须已经 OpenClipboard</b>。
/// 只含 Win32 数据搬运,不含策略(去重、过滤在功能里)。
/// </summary>
internal static class ClipboardFormatsIO
{
    /// <summary>单次复制超过这个总大小不记录(参考 MAIO:200 MB)。</summary>
    public const long MaxTotalBytes = 200L * 1024 * 1024;

    private static readonly uint RtfFormat = RegisterClipboardFormatW("Rich Text Format");
    private static readonly uint HtmlFormat = RegisterClipboardFormatW("HTML Format");
    private static readonly uint ExcludeFromMonitorFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    /// <summary>
    /// 来源程序是否声明"不要记录"。密码管理器等遵循 Windows 约定的程序会设置这两个格式之一。
    /// </summary>
    public static bool IsMarkedAsExcluded()
    {
        if (IsClipboardFormatAvailable(ExcludeFromMonitorFormat))
        {
            return true;
        }

        if (IsClipboardFormatAvailable(CanIncludeInHistoryFormat))
        {
            var value = ReadBytes(CanIncludeInHistoryFormat);
            return value is { Length: >= 4 } && BitConverter.ToInt32(value, 0) == 0;
        }

        return false;
    }

    /// <summary>读取当前剪贴板的全部受支持格式。总大小超限返回 null 并置 <paramref name="tooLarge"/>。</summary>
    public static ClipboardContent? Read(out bool tooLarge)
    {
        tooLarge = false;
        uint[] formats = [CfUnicodeText, CfDib, RtfFormat, HtmlFormat];
        long total = formats.Where(IsClipboardFormatAvailable).Sum(SizeOf);
        if (total > MaxTotalBytes)
        {
            tooLarge = true;
            return null;
        }

        var text = IsClipboardFormatAvailable(CfUnicodeText) ? ReadUnicodeText() : null;
        var files = IsClipboardFormatAvailable(CfHdrop) ? ReadFiles() : [];
        var image = IsClipboardFormatAvailable(CfDib) ? ReadBytes(CfDib) : null;
        var rtf = IsClipboardFormatAvailable(RtfFormat) ? TrimTrailingNul(ReadBytes(RtfFormat)) : null;
        var html = IsClipboardFormatAvailable(HtmlFormat) ? TrimTrailingNul(ReadBytes(HtmlFormat)) : null;

        return new ClipboardContent { Text = text, Files = files, Image = image, Rtf = rtf, Html = html };
    }

    /// <summary>清空并写入内容里所有非空格式。</summary>
    public static bool Write(ClipboardContent content)
    {
        // 先 Empty 再写;调用方已 Open
        if (!EmptyClipboard())
        {
            return false;
        }

        var ok = true;
        if (!string.IsNullOrEmpty(content.Text))
        {
            ok &= Set(CfUnicodeText, Encoding.Unicode.GetBytes(content.Text + "\0"));
        }

        if (content.Files.Count > 0)
        {
            ok &= Set(CfHdrop, BuildDropFiles(content.Files));
        }

        if (content.Image is { Length: > 0 })
        {
            ok &= Set(CfDib, content.Image);
        }

        if (content.Rtf is { Length: > 0 })
        {
            ok &= Set(RtfFormat, [.. content.Rtf, 0]);
        }

        if (content.Html is { Length: > 0 })
        {
            ok &= Set(HtmlFormat, [.. content.Html, 0]);
        }

        return ok;
    }

    private static bool Set(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)data.Length);
        if (memory == 0)
        {
            return false;
        }

        var target = GlobalLock(memory);
        if (target == 0)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory); // 设置成功后内存归系统所有;失败才需要自己释放
            return false;
        }

        return true;
    }

    /// <summary>DROPFILES 结构(20 字节)+ 以双 NUL 结尾的宽字符路径列表。</summary>
    internal static byte[] BuildDropFiles(IReadOnlyList<string> files)
    {
        var header = new byte[20];
        BitConverter.GetBytes(20).CopyTo(header, 0); // pFiles:路径列表相对结构起点的偏移
        BitConverter.GetBytes(1).CopyTo(header, 16); // fWide = TRUE
        var paths = Encoding.Unicode.GetBytes(string.Join('\0', files) + "\0\0");
        return [.. header, .. paths];
    }

    private static long SizeOf(uint format)
    {
        var handle = GetClipboardData(format);
        return handle == 0 ? 0 : (long)GlobalSize(handle);
    }

    private static string? ReadUnicodeText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == 0)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == 0)
        {
            return null;
        }

        var size = (int)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        if (pointer == 0 || size <= 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static IReadOnlyList<string> ReadFiles()
    {
        var handle = GetClipboardData(CfHdrop);
        if (handle == 0)
        {
            return [];
        }

        var count = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFileW(handle, i, null, 0);
            var buffer = new StringBuilder((int)length + 1);
            DragQueryFileW(handle, i, buffer, length + 1);
            files.Add(buffer.ToString());
        }

        return files;
    }

    private static byte[]? TrimTrailingNul(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
        {
            end--;
        }

        return end == bytes.Length ? bytes : bytes[..end];
    }
}
