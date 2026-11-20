namespace Waio.Kit.Services;

public enum ClipboardContentKind
{
    Text,
    Image,
    Files,
}

/// <summary>
/// 一次复制的全部内容。同一次复制常常带多种格式(Word 复制会同时有纯文本、RTF、HTML),
/// 所以各格式并列保存;"完全相同"的判定要看所有格式(见剪贴板功能的去重规则,参考 MAIO)。
/// 平台无关:图片用 DIB 字节(Windows 的 CF_DIB),WinUI 加个 14 字节文件头就能直接显示成 BMP。
/// </summary>
public sealed record ClipboardContent
{
    /// <summary>纯文本。</summary>
    public string? Text { get; init; }

    /// <summary>文件/文件夹的完整路径(复制文件时)。</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>图片,DIB 字节(CF_DIB:BITMAPINFOHEADER + 像素)。</summary>
    public byte[]? Image { get; init; }

    /// <summary>富文本 RTF 原始字节。</summary>
    public byte[]? Rtf { get; init; }

    /// <summary>HTML 片段(Windows 的 "HTML Format",含 CF_HTML 头)。</summary>
    public byte[]? Html { get; init; }

    /// <summary>主类型:有文件算文件,否则有图片算图片,否则文本。</summary>
    public ClipboardContentKind Kind =>
        Files.Count > 0 ? ClipboardContentKind.Files
        : Image is { Length: > 0 } ? ClipboardContentKind.Image
        : ClipboardContentKind.Text;

    public bool IsEmpty =>
        string.IsNullOrEmpty(Text) && Files.Count == 0 && Image is not { Length: > 0 } && Rtf is not { Length: > 0 } && Html is not { Length: > 0 };

    public static ClipboardContent FromText(string text) => new() { Text = text };

    public static ClipboardContent FromFiles(IReadOnlyList<string> files) => new() { Files = files };

    /// <summary>只保留纯文本(用于"粘贴为纯文本")。没有文本时返回空内容。</summary>
    public ClipboardContent AsPlainText() => new() { Text = Text };
}

/// <param name="SourceApp">写入剪贴板的程序名(不含 .exe);未知为 null。</param>
public sealed record ClipboardChange(ClipboardContent Content, string? SourceApp);

/// <summary>
/// 系统剪贴板。监听与读写都在这里,功能不直接碰 Win32 剪贴板 API,也就不会各自重复处理
/// "剪贴板被占用要重试""程序标记不要记录的内容要跳过""自己写入的不要再收一遍"这些坑。
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// 剪贴板内容变化。<b>在剪贴板线程上同步触发,处理器必须立即返回</b>(把内容放进队列,别在里面做 IO)。
    /// 不会触发的情况:WAIO 自己通过 <see cref="SetAsync"/> 写入的;内容过大;没有任何支持的格式;
    /// 来源程序声明"不要记录"且设置要求遵守时(见 <see cref="RespectExclusionFormats"/>)。
    /// </summary>
    event Action<ClipboardChange> Changed;

    /// <summary>
    /// 是否遵守程序的"不要记录"声明(Windows 标准格式 ExcludeClipboardContentFromMonitorProcessing、
    /// CanIncludeInClipboardHistory=0——密码管理器等会设置)。默认 true。可在运行时修改,立即生效。
    /// </summary>
    bool RespectExclusionFormats { get; set; }

    /// <summary>
    /// 写入剪贴板(所有非空格式一并写入)。返回 false 表示剪贴板一直被其他程序占用而写入失败。
    /// 不会触发 <see cref="Changed"/>。
    /// </summary>
    ValueTask<bool> SetAsync(ClipboardContent content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 像用户自己复制一样写入剪贴板:和 <see cref="SetAsync"/> 一样写入,但<b>会触发</b> <see cref="Changed"/>(来源程序是 WAIO),
    /// 所以剪贴板历史会记录(ADR-0015)。例:截图复制的图片、取色复制的颜色值——MAIO 里它们都进剪贴板历史。
    /// 返回 false 表示剪贴板一直被占用而写入失败。
    /// </summary>
    ValueTask<bool> CopyAsync(ClipboardContent content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 剪贴板序号:任何程序(包括 WAIO 自己)每写入一次就变一次。用来判断"刚才模拟的 Ctrl+C 有没有生效"(ADR-0014)。
    /// 任意线程可读。
    /// </summary>
    uint ChangeCount { get; }

    /// <summary>
    /// 读取剪贴板当前内容(只含 <see cref="ClipboardContent"/> 支持的格式)。剪贴板一直被占用或内容过大时返回 null。
    /// </summary>
    ValueTask<ClipboardContent?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始一段"临时借用剪贴板"(ADR-0014):从现在到返回的对象释放为止,其他程序写入的内容<b>不触发</b> <see cref="Changed"/>,
    /// 所以剪贴板历史不会记录。例:翻译读选中文字时模拟 Ctrl+C,读完把原内容写回。
    /// 释放时已经发生、但还没处理完的写入也算在内。
    /// </summary>
    IDisposable SuppressChanges();
}
