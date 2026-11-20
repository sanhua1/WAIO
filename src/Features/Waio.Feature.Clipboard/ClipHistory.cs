using Waio.Feature.Clipboard.Domain;
using Waio.Feature.Clipboard.Infrastructure;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard;

internal enum RecordOutcome
{
    /// <summary>新增了一条。</summary>
    Added,

    /// <summary>已有相同内容,只是移到了最前。</summary>
    Bumped,

    /// <summary>不记录(空内容、被设置过滤掉等)。</summary>
    Ignored,
}

internal readonly record struct RecordResult(RecordOutcome Outcome, long? Id);

/// <summary>
/// 历史的业务规则(参考 MAIO):
/// <list type="number">
/// <item>按设置过滤格式(富文本/图片/文件),过滤后没内容或只有空白就不记;</item>
/// <item>所有格式完全相同的条目 → 只保留一条,移到最前;</item>
/// <item>最近 10 条里"内容相同、格式不同"的(纯文本/文件/图片相同)→ 有固定的就只把它移到最前,否则删旧留新;</item>
/// <item>同一程序 10 秒内连续复制、文字互相包含(终端"选中即复制"拖动选区)→ 只留最新;</item>
/// <item>写入后按存储上限清理最久没用的(固定的不删)。</item>
/// </list>
/// 线程模型:调用方保证 <see cref="Record"/> 串行(功能里有单线程的后台消费者);其余方法可在任意线程调用(存储自己加锁)。
/// </summary>
internal sealed class ClipHistory(IClipStore store, Func<ClipboardSettings> settings, TimeProvider clock)
{
    /// <summary>判断"内容相同"时只看最近这么多条。</summary>
    public const int RecentDuplicateWindow = 10;

    public static readonly TimeSpan SelectionGrowthWindow = TimeSpan.FromSeconds(10);

    /// <summary>列表一次加载的未固定条目数;更早的需要搜索才能找到。</summary>
    public const int ListLimit = 300;

    public RecordResult Record(ClipboardChange change)
    {
        var options = settings();
        var content = Filter(change.Content, options);
        var meaningful = !string.IsNullOrWhiteSpace(content.Text) || content.Files.Count > 0 || content.Image is { Length: > 0 };
        if (!meaningful)
        {
            // 空内容、只有空白、或只剩 RTF/HTML 而没有可显示的文字——都没有可展示的东西
            return new RecordResult(RecordOutcome.Ignored, null);
        }

        var now = clock.GetUtcNow();
        var exactHash = ClipContents.ExactHash(content);
        if (store.FindByExactHash(exactHash) is { } same)
        {
            store.Touch(same, now);
            return new RecordResult(RecordOutcome.Bumped, same);
        }

        var key = ClipContents.ContentKey(content);
        var obsolete = new List<long>();
        if (key is not null)
        {
            var duplicates = store.Recent(RecentDuplicateWindow).Where(r => r.ContentKey == key).ToList();
            if (duplicates.FirstOrDefault(d => d.IsPinned) is { } pinned)
            {
                store.Touch(pinned.Id, now); // 固定的条目不能被新版本顶掉,只移到最前
                return new RecordResult(RecordOutcome.Bumped, pinned.Id);
            }

            obsolete.AddRange(duplicates.Select(d => d.Id));
        }

        if (SelectionPredecessor(content, change.SourceApp, now) is { } predecessor && !obsolete.Contains(predecessor))
        {
            obsolete.Add(predecessor);
        }

        store.Delete(obsolete);
        var id = store.Insert(new NewClip(
            content, ClipContents.KindOf(content), ClipContents.SummaryOf(content), change.SourceApp,
            exactHash, key, ClipContents.ByteSize(content), ClipContents.SearchTextOf(content), now));

        Enforce(options.StorageLimit);
        return new RecordResult(RecordOutcome.Added, id);
    }

    /// <summary>列表(<paramref name="query"/> 为空)或搜索结果:多个词都要出现,不区分大小写。</summary>
    public IReadOnlyList<ClipItem> Load(string? query = null)
    {
        var terms = (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.Length == 0 ? store.List(ListLimit) : store.Search(terms, ListLimit);
    }

    public ClipboardContent? Content(long id) => store.LoadContent(id);

    /// <summary>只读图片数据(DIB),列表缩略图用。</summary>
    public byte[]? Image(long id) => store.LoadImage(id);

    /// <summary>粘贴后移到最前。</summary>
    public void MarkUsed(long id) => store.Touch(id, clock.GetUtcNow());

    public void SetPinned(long id, bool pinned) => store.SetPinned(id, pinned);

    public void Delete(long id) => store.Delete([id]);

    /// <summary>清空历史,固定的保留。</summary>
    public void ClearUnpinned() => store.DeleteUnpinned();

    /// <summary>按当前设置的上限清理一次(设置变小时调用)。</summary>
    public void Enforce(StorageLimit limit) => EnforceBytes(limit.Bytes());

    /// <summary>按字节上限清理(测试可以用很小的数字,枚举里最小的档位是 500 MB)。</summary>
    internal void EnforceBytes(long? limit) =>
        store.Delete([.. RetentionPolicy.OverLimit([.. store.Entries()], limit)]);

    private static ClipboardContent Filter(ClipboardContent content, ClipboardSettings options) =>
        content with
        {
            Rtf = options.RecordRichText ? content.Rtf : null,
            Html = options.RecordRichText ? content.Html : null,
            Image = options.RecordImages ? content.Image : null,
            Files = options.RecordFiles ? content.Files : [],
        };

    private long? SelectionPredecessor(ClipboardContent content, string? source, DateTimeOffset now)
    {
        if (source is null || string.IsNullOrEmpty(content.Text) || content.Files.Count > 0)
        {
            return null;
        }

        var latest = store.Recent(1).FirstOrDefault();
        if (latest is null || latest.IsPinned || latest.SourceApp != source || now - latest.CreatedAt > SelectionGrowthWindow)
        {
            return null;
        }

        var oldText = store.LoadContent(latest.Id)?.Text;
        return oldText is not null && ClipText.IsSelectionGrowth(oldText, content.Text) ? latest.Id : null;
    }
}
