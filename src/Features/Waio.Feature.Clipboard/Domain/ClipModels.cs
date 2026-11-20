using System.Security.Cryptography;
using System.Text;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard.Domain;

/// <summary>条目类型,决定列表怎么显示。</summary>
public enum ClipKind
{
    Text,
    RichText,
    Image,
    Files,
}

/// <summary>一条历史记录的元数据(列表显示用,不含完整数据)。</summary>
/// <param name="HasImage">带图片数据(纯图片,或 PixPin 这类同时给出文件和图片的复制)——列表据此显示缩略图。</param>
internal sealed record ClipItem(
    long Id,
    ClipKind Kind,
    string Summary,
    string? SourceApp,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    long ByteSize,
    bool IsPinned,
    bool HasImage = false);

/// <summary>文本相关的纯函数(参考 MAIO 的 ClipText)。</summary>
internal static class ClipText
{
    /// <summary>
    /// 比较内容时忽略的差别:换行符(\r\n、\r 统一成 \n)、首尾空白。
    /// 同一段文字从不同来源复制,换行风格和首尾空白常常不同。
    /// </summary>
    public static string Normalized(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>
    /// 新复制的文字是不是"同一次选择的延续":一段包含另一段。
    /// 终端"选中即复制"时,拖动选区的过程中每变一下就复制一次,产生一串长短不一的版本。
    /// </summary>
    public static bool IsSelectionGrowth(string oldText, string newText)
    {
        var old = Normalized(oldText);
        var next = Normalized(newText);
        return old.Length > 0 && next.Length > 0
            && (next.Contains(old, StringComparison.Ordinal) || old.Contains(next, StringComparison.Ordinal));
    }

    /// <summary>合并空白、截断,用于列表里一行显示。</summary>
    public static string Summary(string text, int limit = 300)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > limit ? collapsed[..limit] + "…" : collapsed;
    }
}

/// <summary>对一次复制内容(<see cref="ClipboardContent"/>)的纯函数:类型、摘要、去重键、大小。</summary>
internal static class ClipContents
{
    /// <summary>搜索用文本最多保留这么多字符,免得一条巨大文本把搜索拖慢。</summary>
    public const int MaxSearchChars = 100_000;

    public static ClipKind KindOf(ClipboardContent content)
    {
        if (content.Files.Count > 0)
        {
            return ClipKind.Files;
        }

        if (!string.IsNullOrEmpty(content.Text))
        {
            return content.Rtf is { Length: > 0 } || content.Html is { Length: > 0 } ? ClipKind.RichText : ClipKind.Text;
        }

        return content.Image is { Length: > 0 } ? ClipKind.Image : ClipKind.Text;
    }

    public static string SummaryOf(ClipboardContent content) =>
        KindOf(content) switch
        {
            ClipKind.Files => content.Files.Count > 1
                ? $"{Path.GetFileName(content.Files[0])} 等 {content.Files.Count} 个文件"
                : Path.GetFileName(content.Files[0].TrimEnd('\\', '/')) is { Length: > 0 } name ? name : content.Files[0],
            ClipKind.Image => "图片",
            _ => ClipText.Summary(content.Text ?? ""),
        };

    /// <summary>
    /// "内容是否相同"的依据(不管格式):文字比较规范化后的纯文本,文件比较路径,图片比较图片数据。
    /// 同一段文字从不同程序复制,附带的 RTF/HTML 往往不同,完整哈希对不上,但用户看来是重复的。
    /// </summary>
    public static string? ContentKey(ClipboardContent content) =>
        KindOf(content) switch
        {
            ClipKind.Files => Sha("files", Encoding.UTF8.GetBytes(string.Join('\n', content.Files))),
            ClipKind.Image => Sha("image", content.Image!),
            _ => string.IsNullOrEmpty(content.Text) ? null : Sha("text", Encoding.UTF8.GetBytes(ClipText.Normalized(content.Text))),
        };

    /// <summary>所有格式合起来的哈希:完全相同(每个格式都一样)才相同。</summary>
    public static string ExactHash(ClipboardContent content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "text", content.Text is null ? [] : Encoding.UTF8.GetBytes(content.Text));
        Append(hash, "files", Encoding.UTF8.GetBytes(string.Join('\n', content.Files)));
        Append(hash, "image", content.Image ?? []);
        Append(hash, "rtf", content.Rtf ?? []);
        Append(hash, "html", content.Html ?? []);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static long ByteSize(ClipboardContent content) =>
        (content.Text is null ? 0 : Encoding.UTF8.GetByteCount(content.Text))
        + content.Files.Sum(f => (long)Encoding.UTF8.GetByteCount(f))
        + (content.Image?.Length ?? 0) + (content.Rtf?.Length ?? 0) + (content.Html?.Length ?? 0);

    /// <summary>搜索用的小写文本(文字或文件路径);图片没有。</summary>
    public static string SearchTextOf(ClipboardContent content)
    {
        var text = content.Files.Count > 0 ? string.Join('\n', content.Files) : content.Text ?? "";
        if (text.Length > MaxSearchChars)
        {
            text = text[..MaxSearchChars];
        }

        return text.ToLowerInvariant();
    }

    private static string Sha(string tag, byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, tag, data);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // 带长度前缀,避免 "ab"+"c" 与 "a"+"bc" 这类拼接歧义
    private static void Append(IncrementalHash hash, string tag, byte[] data)
    {
        hash.AppendData(Encoding.ASCII.GetBytes(tag));
        hash.AppendData(BitConverter.GetBytes(data.Length));
        hash.AppendData(data);
    }
}
