using System.Text;

namespace Waio.Feature.Launcher.Domain;

internal enum PreviewKind
{
    /// <summary>没有预览:只显示类型和"没有预览"。</summary>
    None,
    Text,
    Image,
}

/// <summary>
/// 右侧预览框的规则:文本、图片直接预览,其他显示文件类型 + "没有预览"。
/// 不用资源管理器的预览处理程序(IPreviewHandler):WAIO 常驻管理员,第三方预览器会以管理员身份解析不可信文件;
/// 文字和图片 WAIO 自己读,所以也没有"此文件可能会损害你的计算机"那一步(网络下载标记不影响我们读字节)。见 ADR-0028。
/// </summary>
internal static class PreviewRules
{
    /// <summary>文本最多读这么多字节(只看开头,够填满预览框;大日志不卡)。</summary>
    public const int MaxTextBytes = 64 * 1024;

    /// <summary>超过这个大小的图片不解码(整个读进内存再解码,省内存、防卡顿)。</summary>
    public const long MaxImageBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".xaml", ".html", ".htm", ".css",
        ".ini", ".cfg", ".conf", ".config", ".toml", ".yaml", ".yml", ".properties", ".env", ".gitignore", ".editorconfig",
        ".cs", ".csproj", ".props", ".targets", ".sln", ".vb", ".fs", ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt",
        ".go", ".rs", ".py", ".rb", ".php", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".vue", ".swift", ".lua", ".sql",
        ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd", ".reg", ".srt", ".ass", ".lrc", ".tex", ".rst", ".diff", ".patch",
    };

    /// <summary>WIC 能解码的常见图片(Windows 10/11 自带解码器)。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".dib", ".ico", ".tif", ".tiff", ".webp",
    };

    private static readonly Lazy<Encoding> Gb18030 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936);
    });

    public static PreviewKind KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension))
        {
            return PreviewKind.Image;
        }

        return TextExtensions.Contains(extension) || TextExtensions.Contains(Path.GetFileName(path)) ? PreviewKind.Text : PreviewKind.None;
    }

    /// <summary>
    /// 把文件开头的字节解成文字:有 BOM 按 BOM;否则先按 UTF-8 严格解,不合法再按 GB18030(中文 Windows 的老文件)。
    /// 含 NUL 的(且不是 UTF-16)当作二进制,返回 null。截断处如果切开了一个多字节字符,丢掉那半个字。
    /// </summary>
    public static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Lenient(new UTF8Encoding(false), bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Lenient(Encoding.Unicode, bytes[2..(2 + ((bytes.Length - 2) & ~1))]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Lenient(Encoding.BigEndianUnicode, bytes[2..(2 + ((bytes.Length - 2) & ~1))]);
        }

        if (bytes.Contains((byte)0))
        {
            return null;
        }

        var utf8 = TrimPartialUtf8(bytes);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return Lenient(Gb18030.Value, bytes);
        }
    }

    /// <summary>末尾被截断的不完整 UTF-8 字符去掉(否则严格解码会误判成"不是 UTF-8")。</summary>
    internal static ReadOnlySpan<byte> TrimPartialUtf8(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue; // 后续字节,继续往前找首字节
            }

            var need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return need > back ? bytes[..^back] : bytes;
        }

        return bytes;
    }

    private static string Lenient(Encoding encoding, ReadOnlySpan<byte> bytes) => encoding.GetString(bytes).TrimEnd('�');

    /// <summary>预览框里显示的大小:"12.3 KB"。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} 字节",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
