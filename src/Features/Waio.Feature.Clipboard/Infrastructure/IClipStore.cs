using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard.Infrastructure;

/// <summary>要写入的一条新记录。元数据由领域层算好,存储层只负责落盘。</summary>
internal sealed record NewClip(
    ClipboardContent Content,
    ClipKind Kind,
    string Summary,
    string? SourceApp,
    string ExactHash,
    string? ContentKey,
    long ByteSize,
    string SearchText,
    DateTimeOffset At);

/// <summary>去重判断用的最近记录(轻量,不含内容)。</summary>
internal sealed record RecentClip(long Id, string? ContentKey, bool IsPinned, string? SourceApp, DateTimeOffset CreatedAt);

/// <summary>
/// 剪贴板历史的存储。同步接口(本地 SQLite 足够快),实现必须线程安全。
/// 领域规则(去重、保留)不在这里,存储只做增删查改。
/// </summary>
internal interface IClipStore : IDisposable
{
    long Insert(NewClip clip);

    long? FindByExactHash(string exactHash);

    /// <summary>最近使用的 <paramref name="count"/> 条,按最后使用时间从新到旧。</summary>
    IReadOnlyList<RecentClip> Recent(int count);

    /// <summary>列表用:所有固定的 + 最近使用的 <paramref name="limit"/> 条未固定的。顺序不保证(由 PickerState 排)。</summary>
    IReadOnlyList<ClipItem> List(int limit);

    /// <summary>所有词都出现在文字/路径里(不区分大小写)。<paramref name="lowerTerms"/> 已是小写。</summary>
    IReadOnlyList<ClipItem> Search(IReadOnlyList<string> lowerTerms, int limit);

    ClipItem? Get(long id);

    /// <summary>读回完整内容;条目不存在或内容全部丢失(blob 文件被删)返回 null。</summary>
    ClipboardContent? LoadContent(long id);

    /// <summary>只读图片数据(DIB),给列表缩略图用;没有图片或数据已丢失返回 null。</summary>
    byte[]? LoadImage(long id);

    void Touch(long id, DateTimeOffset at);

    void SetPinned(long id, bool pinned);

    void Delete(IReadOnlyCollection<long> ids);

    /// <summary>删除所有未固定的。</summary>
    void DeleteUnpinned();

    IReadOnlyList<RetentionPolicy.Entry> Entries();
}
