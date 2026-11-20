using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Clipboard;
using Waio.Feature.Clipboard.Domain;
using Waio.Feature.Clipboard.Infrastructure;
using Waio.Kit.Services;

namespace Waio.Feature.Clipboard.Tests;

/// <summary>手动推进的时钟,测试"10 秒内""最近使用"这类时间规则。</summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>每个测试一个独立的临时目录和真实的 SQLite 库。</summary>
public sealed class ClipHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-clip-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private readonly ClipboardSettings _settings = new();
    private readonly SqliteClipStore _store;
    private readonly ClipHistory _history;

    public ClipHistoryTests()
    {
        _store = SqliteClipStore.Open(_dir, NullLogger.Instance);
        _history = new ClipHistory(_store, () => _settings, _clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结果
        }
    }

    private RecordResult Copy(string text, string? app = "notepad") =>
        _history.Record(new ClipboardChange(ClipboardContent.FromText(text), app));

    private RecordResult Copy(ClipboardContent content, string? app = "notepad") =>
        _history.Record(new ClipboardChange(content, app));

    private string[] Summaries(string? query = null) =>
        [.. PickerState.Arrange(_history.Load(query)).Select(i => i.Summary)];

    private string BlobDir => Path.Combine(_dir, "blobs");

    // ---- 记录与去重 ----

    [Fact]
    public void 新内容_被记录_出现在列表里()
    {
        var result = Copy("hello");

        Assert.Equal(RecordOutcome.Added, result.Outcome);
        Assert.Equal(["hello"], Summaries());
    }

    [Fact]
    public void 完全相同再次复制_只保留一条_移到最前()
    {
        Copy("first");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Copy("second");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var again = Copy("first");

        Assert.Equal(RecordOutcome.Bumped, again.Outcome);
        Assert.Equal(["first", "second"], Summaries());
    }

    [Fact]
    public void 文字相同但格式不同_删旧留新()
    {
        Copy("same text");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = Copy(new ClipboardContent { Text = "same text", Rtf = [1, 2, 3] });

        Assert.Equal(RecordOutcome.Added, result.Outcome);
        var items = _history.Load();
        var only = Assert.Single(items);
        Assert.Equal(ClipKind.RichText, only.Kind);
    }

    [Fact]
    public void 内容相同的旧条目被固定_新版本不保存_只把固定的移到最前()
    {
        var first = Copy("pinned text");
        _history.SetPinned(first.Id!.Value, true);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = Copy(new ClipboardContent { Text = "pinned text", Rtf = [9] });

        Assert.Equal(RecordOutcome.Bumped, result.Outcome);
        Assert.Equal(first.Id, result.Id);
        var only = Assert.Single(_history.Load());
        Assert.True(only.IsPinned);
        Assert.Equal(ClipKind.Text, only.Kind); // 还是原来的版本,没有被富文本版本取代
    }

    [Fact]
    public void 超出最近10条窗口的同内容_不按格式去重()
    {
        Copy("target");
        for (var i = 0; i < ClipHistory.RecentDuplicateWindow; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Copy($"filler {i}");
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        Copy(new ClipboardContent { Text = "target", Rtf = [1] });

        Assert.Equal(2, _history.Load().Count(i => i.Summary == "target"));
    }

    [Fact]
    public void 空白和空内容_不记录()
    {
        Assert.Equal(RecordOutcome.Ignored, Copy("   \r\n\t").Outcome);
        Assert.Equal(RecordOutcome.Ignored, Copy(new ClipboardContent()).Outcome);
        Assert.Equal(RecordOutcome.Ignored, Copy(new ClipboardContent { Rtf = [1] }).Outcome);
        Assert.Empty(_history.Load());
    }

    // ---- 终端"选中即复制"的选区增长 ----

    [Fact]
    public void 同一程序10秒内文字互相包含_只留最新()
    {
        Copy("hel", app: "terminal");
        _clock.Advance(TimeSpan.FromSeconds(2));
        Copy("hello wor", app: "terminal");
        _clock.Advance(TimeSpan.FromSeconds(2));
        Copy("hello world", app: "terminal");

        Assert.Equal(["hello world"], Summaries());
    }

    [Fact]
    public void 不同程序_不按选区增长合并()
    {
        Copy("hello", app: "terminal");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Copy("hello world", app: "notepad");

        Assert.Equal(2, _history.Load().Count);
    }

    [Fact]
    public void 超过10秒_不按选区增长合并()
    {
        Copy("hello", app: "terminal");
        _clock.Advance(TimeSpan.FromSeconds(11));
        Copy("hello world", app: "terminal");

        Assert.Equal(2, _history.Load().Count);
    }

    [Fact]
    public void 上一条被固定_不会被选区增长顶掉()
    {
        var first = Copy("hello", app: "terminal");
        _history.SetPinned(first.Id!.Value, true);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Copy("hello world", app: "terminal");

        Assert.Equal(2, _history.Load().Count);
    }

    [Fact]
    public void 来源未知_不做选区增长合并()
    {
        Copy("hello", app: null);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Copy("hello world", app: null);

        Assert.Equal(2, _history.Load().Count);
    }

    // ---- 按设置过滤 ----

    [Fact]
    public void 关闭记录富文本_RTF和HTML不保存()
    {
        _settings.RecordRichText = false;

        var id = Copy(new ClipboardContent { Text = "t", Rtf = [1], Html = [2] }).Id!.Value;

        var content = _history.Content(id)!;
        Assert.Null(content.Rtf);
        Assert.Null(content.Html);
        Assert.Equal("t", content.Text);
        Assert.Equal(ClipKind.Text, _history.Load().Single().Kind);
    }

    [Fact]
    public void 关闭记录图片_只有图片的复制被忽略()
    {
        _settings.RecordImages = false;

        Assert.Equal(RecordOutcome.Ignored, Copy(new ClipboardContent { Image = [1, 2, 3] }).Outcome);
    }

    [Fact]
    public void 关闭记录文件_文件列表被忽略()
    {
        _settings.RecordFiles = false;

        Assert.Equal(RecordOutcome.Ignored, Copy(ClipboardContent.FromFiles([@"C:\a.txt"])).Outcome);
    }

    // ---- 搜索 ----

    [Fact]
    public void 搜索_所有词都要出现_不区分大小写()
    {
        Copy("Hello World");
        Copy("hello there");
        Copy("another WORLD");

        Assert.Equal(["Hello World"], Summaries("HELLO world"));
        Assert.Equal(2, Summaries("hello").Length);
        Assert.Empty(Summaries("hello nothing"));
    }

    [Fact]
    public void 搜索_百分号下划线反斜杠按字面匹配()
    {
        Copy("100% done");
        Copy("a_b");
        Copy("axb");
        Copy(@"C:\dir\file");

        Assert.Equal(["100% done"], Summaries("100%"));
        Assert.Equal(["a_b"], Summaries("a_b"));
        Assert.Equal([@"C:\dir\file"], Summaries(@"c:\dir"));
    }

    [Fact]
    public void 搜索_能搜到文件路径()
    {
        Copy(ClipboardContent.FromFiles([@"C:\Projects\report.docx"]));

        Assert.Single(Summaries("projects"));
    }

    [Fact]
    public void 搜索_中文可用()
    {
        Copy("今天的会议纪要");

        Assert.Single(Summaries("会议"));
    }

    // ---- 固定、使用、删除、清空 ----

    [Fact]
    public void 粘贴后_移到最前()
    {
        var old = Copy("old").Id!.Value;
        _clock.Advance(TimeSpan.FromMinutes(1));
        Copy("new");
        _clock.Advance(TimeSpan.FromMinutes(1));

        _history.MarkUsed(old);

        Assert.Equal(["old", "new"], Summaries());
    }

    [Fact]
    public void 清空_固定的保留()
    {
        var keep = Copy("keep").Id!.Value;
        _history.SetPinned(keep, true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Copy("drop");

        _history.ClearUnpinned();

        Assert.Equal(["keep"], Summaries());
    }

    [Fact]
    public void 删除一条()
    {
        var id = Copy("gone").Id!.Value;
        Copy("stay");

        _history.Delete(id);

        Assert.Equal(["stay"], Summaries());
        Assert.Null(_history.Content(id));
    }

    // ---- 内容往返与大数据 ----

    [Fact]
    public void 多种格式_存取后完全一致()
    {
        var original = new ClipboardContent
        {
            Text = "文本 text",
            Rtf = [1, 2, 3],
            Html = [4, 5],
            Image = [6, 7, 8, 9],
        };

        var id = Copy(original).Id!.Value;

        var loaded = _history.Content(id)!;
        Assert.Equal(original.Text, loaded.Text);
        Assert.Equal(original.Rtf, loaded.Rtf);
        Assert.Equal(original.Html, loaded.Html);
        Assert.Equal(original.Image, loaded.Image);
    }

    [Fact]
    public void 文件列表_存取后一致()
    {
        var files = new[] { @"C:\a b\c.txt", @"D:\中文\d.png" };

        var id = Copy(ClipboardContent.FromFiles(files)).Id!.Value;

        Assert.Equal(files, _history.Content(id)!.Files);
    }

    [Fact]
    public void 大数据存成blob文件_读回一致_删除后文件被清理()
    {
        var big = new byte[SqliteClipStore.InlineLimit + 1000];
        new Random(1).NextBytes(big);

        var id = Copy(new ClipboardContent { Image = big }).Id!.Value;

        Assert.Single(Directory.GetFiles(BlobDir));
        Assert.Equal(big, _history.Content(id)!.Image);

        _history.Delete(id);
        Assert.Empty(Directory.GetFiles(BlobDir));
    }

    [Fact]
    public void 小数据不产生blob文件()
    {
        Copy(new ClipboardContent { Image = new byte[100] });

        Assert.Empty(Directory.GetFiles(BlobDir));
    }

    [Fact]
    public void 两条记录共用同一个blob_删一条文件还在_删完才清理()
    {
        var shared = new byte[SqliteClipStore.InlineLimit + 10];
        new Random(2).NextBytes(shared);
        var a = Copy(new ClipboardContent { Text = "a", Rtf = shared }).Id!.Value;
        var b = Copy(new ClipboardContent { Text = "b", Rtf = shared }).Id!.Value;
        Assert.Single(Directory.GetFiles(BlobDir));

        _history.Delete(a);
        Assert.Single(Directory.GetFiles(BlobDir));
        Assert.NotNull(_history.Content(b)!.Rtf);

        _history.Delete(b);
        Assert.Empty(Directory.GetFiles(BlobDir));
    }

    [Fact]
    public void blob文件丢了_其他格式仍可读_不抛异常()
    {
        var big = new byte[SqliteClipStore.InlineLimit + 10];
        var id = Copy(new ClipboardContent { Text = "keep me", Rtf = big }).Id!.Value;
        foreach (var file in Directory.GetFiles(BlobDir))
        {
            File.Delete(file);
        }

        var loaded = _history.Content(id)!;

        Assert.Equal("keep me", loaded.Text);
        Assert.Null(loaded.Rtf);
    }

    // ---- 保留策略 ----

    [Fact]
    public void 超过上限_删最久没用的_固定的保留()
    {
        var pinned = Copy(new ClipboardContent { Text = "pinned", Image = new byte[1000] }).Id!.Value;
        _history.SetPinned(pinned, true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Copy(new ClipboardContent { Text = "oldest", Image = new byte[1000] });
        _clock.Advance(TimeSpan.FromMinutes(1));
        Copy(new ClipboardContent { Text = "newest", Image = new byte[1000] });

        _history.EnforceBytes(2200); // 三条各约 1006 字节,必须删掉一条未固定的

        Assert.Equal(["newest", "pinned"], Summaries());
    }

    [Fact]
    public void 记录时自动按设置的上限清理()
    {
        // 默认 2 GB,远大于测试数据:不应该误删
        Copy("a");
        Copy("b");

        Assert.Equal(2, _history.Load().Count);
    }

    // ---- 数据库损坏 ----

    [Fact]
    public void 数据库文件损坏_备份并重建_不抛异常()
    {
        var dir = Path.Combine(_dir, "corrupt");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "history.sqlite"), "this is definitely not a sqlite database file, padding padding padding");

        using var store = SqliteClipStore.Open(dir, NullLogger.Instance);
        var history = new ClipHistory(store, () => _settings, _clock);
        history.Record(new ClipboardChange(ClipboardContent.FromText("after rebuild"), null));

        Assert.Single(history.Load());
        Assert.Single(Directory.GetFiles(dir, "history.sqlite.bad-*"));
    }

    [Fact]
    public void 重新打开同一个库_数据还在()
    {
        var id = Copy("persisted").Id!.Value;
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = SqliteClipStore.Open(_dir, NullLogger.Instance);

        Assert.Equal("persisted", reopened.LoadContent(id)!.Text);
    }
}
