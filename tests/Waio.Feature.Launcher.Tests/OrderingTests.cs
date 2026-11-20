using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Panel;
using Waio.Testing;
using Waio.UI;
using static Waio.Feature.Launcher.Tests.RankingTests;

namespace Waio.Feature.Launcher.Tests;

/// <summary>
/// "全部"的顺序:软件 > 文件 > 文件夹 > 网页搜索;文件结果没到之前不出现网页搜索,默认选中第一项
/// (想搜文件,默认却选中了联网搜索)。
/// </summary>
public class OrderingTests
{
    private static readonly string Dir = Path.GetTempPath();

    private static LauncherItem File(string name) => LauncherItem.File(Path.Combine(Dir, name), name, isDirectory: false);

    private static LauncherItem Folder(string name) => LauncherItem.File(Path.Combine(Dir, name), name, isDirectory: true);

    [Fact]
    public void 全部_软件_文件_文件夹_网页搜索()
    {
        var files = new FileResults("report", [Folder("report 2024"), File("report.docx"), File("report.pdf")], 3);
        var ranking = new LauncherRanking([App("Report Viewer")], new UsageBook(), new LauncherSettings(), DateTimeOffset.Now, files);

        var sections = ranking.Sections(new LauncherQuery(LauncherSource.All, "report", null));

        Assert.Equal(["应用", "文件", "文件夹", "网页搜索"], sections.Select(s => s.Title));
        Assert.Equal(["report.docx", "report.pdf"], sections[1].Items.Select(i => i.Title));
    }

    [Fact]
    public void 共N个_写在第一个文件组上()
    {
        var files = new FileResults("x", [Folder("x1"), File("x2")], 500);

        var sections = new LauncherRanking([], new UsageBook(), new LauncherSettings(), DateTimeOffset.Now, files)
            .Sections(new LauncherQuery(LauncherSource.Files, "x", null));

        Assert.Equal(["文件(共 500 个)", "文件夹"], sections.Select(s => s.Title));
        Assert.DoesNotContain(sections.SelectMany(s => s.Items), i => i.Kind == LauncherItemKind.MoreFiles); // "文件"标签里不加
    }

    [Fact]
    public void 全部里没列全_最后一组末尾加查看全部_网页搜索仍在最后()
    {
        var files = new FileResults("x", [Folder("x1"), File("x2")], 120);

        var sections = new LauncherRanking([], new UsageBook(), new LauncherSettings(), DateTimeOffset.Now, files)
            .Sections(new LauncherQuery(LauncherSource.All, "x", null));

        Assert.Equal(["文件(共 120 个)", "文件夹", "网页搜索"], sections.Select(s => s.Title));
        var more = sections[1].Items[^1];
        Assert.Equal(LauncherItemKind.MoreFiles, more.Kind);
        Assert.Equal("查看全部 120 个", more.Title);
        Assert.Null(more.Path);
    }

    [Fact]
    public void 总数超过文件标签上限_如实写前300个()
    {
        var files = new FileResults("x", [File("x2")], 5000);

        var sections = new LauncherRanking([], new UsageBook(), new LauncherSettings(), DateTimeOffset.Now, files)
            .Sections(new LauncherQuery(LauncherSource.All, "x", null));

        Assert.Equal("查看前 300 个(共 5,000 个)", sections[0].Items[^1].Title);
    }

    [Fact]
    public void 全部列全了_不加查看全部()
    {
        var files = new FileResults("x", [Folder("x1"), File("x2")], 2);

        var items = new LauncherRanking([], new UsageBook(), new LauncherSettings(), DateTimeOffset.Now, files)
            .Sections(new LauncherQuery(LauncherSource.All, "x", null)).SelectMany(s => s.Items);

        Assert.DoesNotContain(items, i => i.Kind == LauncherItemKind.MoreFiles);
    }

    [Fact]
    public async Task 打开查看全部_切到文件标签_输入不变_不启动也不记常用度()
    {
        var shown = new FileResults("report", [File("report.docx")], 50);
        var launcher = new RecordingLauncher();
        var state = new LauncherState();
        var presenter = new LauncherPresenter(
            state, () => new LauncherSettings { AutoSwitchToFiles = false }, new NullView(), new NullSystem(), launcher, new FakeClipboard(),
            new RecordingOverlay(), _ => { }, NullLogger.Instance, new SlowSearch(Task.CompletedTask, shown));
        presenter.Show(LauncherSource.All);
        presenter.OnQueryChanged("report");
        await presenter.PendingFileSearch;
        var index = state.Items.ToList().FindIndex(i => i.Kind == LauncherItemKind.MoreFiles);
        Assert.True(index >= 0);

        await presenter.OpenAtAsync(index);

        Assert.Equal(LauncherSource.Files, state.EffectiveSource);
        Assert.Equal("report", state.Query);
        Assert.Empty(launcher.Requests);
        Assert.Empty(state.Usage.Items);
        Assert.Equal(0, state.SelectedIndex);
    }

    [Fact]
    public void 文件结果没到_不显示网页搜索_到了以后选中第一个文件()
    {
        var state = new LauncherState { Settings = new LauncherSettings { AutoSwitchToFiles = false } }; // 留在"全部"才有网页搜索
        state.SetApps([App("微信")]);

        state.SetQuery("report");
        Assert.True(state.FilesPending);
        Assert.Empty(state.Items); // 没有匹配的应用,也没有网页搜索可以被误选

        state.SetFiles(new FileResults("report", [File("report.docx")], 1));

        Assert.False(state.FilesPending);
        Assert.Equal("report.docx", state.Selected?.Title);
        Assert.Equal(LauncherItemKind.Web, state.Items[^1].Kind);
    }

    [Fact]
    public void 上一次输入的结果还在时_新输入也算没到()
    {
        var state = new LauncherState();
        state.SetQuery("rep");
        state.SetFiles(new FileResults("rep", [File("rep.txt")], 1));

        state.SetQuery("repo");

        Assert.True(state.FilesPending);
        Assert.DoesNotContain(state.Items, i => i.Kind == LauncherItemKind.Web);
    }

    [Fact]
    public void 用户自己移动过_结果更新后保持在那一项()
    {
        var state = new LauncherState();
        state.SetQuery("report");
        state.SetFiles(new FileResults("report", [File("a report.txt"), File("b report.txt")], 2));
        state.Move(1);

        state.SetApps([App("Report Tool")]); // 应用列表刷新,应用排到最前面

        Assert.Equal("b report.txt", state.Selected?.Title);
    }

    [Fact]
    public void 关掉文件搜索_网页搜索马上出现()
    {
        var state = new LauncherState { Settings = new LauncherSettings { FileSearchEnabled = false } };

        state.SetQuery("report");

        Assert.False(state.FilesPending);
        Assert.Equal(LauncherItemKind.Web, Assert.Single(state.Items).Kind);
    }

    [Fact]
    public async Task 回车时文件结果还没到_等它到了再打开第一个文件()
    {
        var name = $"waio-report-{Guid.NewGuid():N}.docx";
        System.IO.File.WriteAllText(Path.Combine(Dir, name), ""); // 打开前会检查文件还在不在
        var gate = new TaskCompletionSource();
        var search = new SlowSearch(gate.Task, new FileResults("report", [File(name)], 1));
        var launcher = new RecordingLauncher();
        var state = new LauncherState();
        var presenter = new LauncherPresenter(
            state, () => new LauncherSettings(), new NullView(), new NullSystem(), launcher, new FakeClipboard(), new RecordingOverlay(),
            _ => { }, NullLogger.Instance, search);
        presenter.Show(LauncherSource.All);
        presenter.OnQueryChanged("report");

        var open = presenter.ExecuteAsync(new LauncherAction(LauncherActionKind.Open));
        await Task.Delay(50);
        gate.SetResult(); // 文件搜索在 300 ms 内完成
        await open;

        System.IO.File.Delete(Path.Combine(Dir, name));
        Assert.EndsWith(name, Assert.Single(launcher.Requests).Target);
    }

    [Fact]
    public async Task 文件搜索失败_网页搜索照常出现()
    {
        var state = new LauncherState();
        var presenter = new LauncherPresenter(
            state, () => new LauncherSettings(), new NullView(), new NullSystem(), new RecordingLauncher(), new FakeClipboard(),
            new RecordingOverlay(), _ => { }, NullLogger.Instance, new SlowSearch(Task.FromException(new IOException("坏了")), null!));
        presenter.Show(LauncherSource.All);
        presenter.OnQueryChanged("report");

        await presenter.PendingFileSearch;

        Assert.Equal(LauncherItemKind.Web, Assert.Single(state.Items).Kind);
    }

    private sealed class SlowSearch(Task gate, FileResults results) : IFileSearch
    {
        public FileResults Search(string text, bool foldersOnly, int limit, CancellationToken cancellation)
        {
            gate.GetAwaiter().GetResult();
            return results;
        }
    }

    private sealed class NullView : ILauncherView
    {
        public bool IsVisible { get; private set; }

        public bool IsActive => IsVisible;

        public void Render(LauncherState state)
        {
        }

        public void SetQuery(string text)
        {
        }

        public void Show(PanelPlacement placement) => IsVisible = true;

        public void Hide() => IsVisible = false;
    }

    private sealed class NullSystem : IPanelSystem
    {
        public nint Foreground() => 1;

        public Task RestoreAsync(nint window) => Task.CompletedTask;

        public void AllowForegroundHandoff()
        {
        }

        public PanelPlacement Locate() => new(new ScreenRect(0, 0, 1920, 1080), 1);
    }
}
