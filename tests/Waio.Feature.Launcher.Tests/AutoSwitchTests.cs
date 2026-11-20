using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Panel;
using Waio.Testing;
using Waio.UI;
using static Waio.Feature.Launcher.Tests.RankingTests;

namespace Waio.Feature.Launcher.Tests;

/// <summary>"全部"里没有匹配的应用、找到了文件 → 自动切到"文件"。</summary>
public class AutoSwitchTests
{
    private static readonly string Dir = Path.GetTempPath();

    private static LauncherItem File(string name) => LauncherItem.File(Path.Combine(Dir, name), name, isDirectory: false);

    private static LauncherState NewState(params LauncherItem[] apps)
    {
        var state = new LauncherState();
        state.SetApps(apps);
        return state;
    }

    [Fact]
    public void 没有应用_文件到了_自动切到文件_标签仍是全部()
    {
        var state = NewState(App("微信"));
        state.SetQuery("report");
        Assert.Equal(LauncherSource.All, state.EffectiveSource); // 文件结果没到之前不切

        state.SetFiles(new FileResults("report", [File("report.docx")], 1));

        Assert.True(state.AutoFiles);
        Assert.Equal(LauncherSource.Files, state.EffectiveSource);
        Assert.Equal(LauncherSource.All, state.SelectedSource);
        Assert.False(state.HasPrefix);
        Assert.DoesNotContain(state.Items, i => i.Kind is LauncherItemKind.Web or LauncherItemKind.MoreFiles);
        Assert.Equal("report.docx", state.Selected?.Title);
    }

    [Fact]
    public void 文件也没有_留在全部_网页搜索还在()
    {
        var state = NewState(App("微信"));
        state.SetQuery("zzqx");

        state.SetFiles(new FileResults("zzqx", [], 0));

        Assert.False(state.AutoFiles);
        Assert.Equal(LauncherSource.All, state.EffectiveSource);
        Assert.Equal(LauncherItemKind.Web, Assert.Single(state.Items).Kind);
    }

    [Fact]
    public void 有匹配的应用_不切_继续输入到没有应用时再切()
    {
        var state = NewState(App("Report Tool"));
        state.SetQuery("report");
        state.SetFiles(new FileResults("report", [File("report.docx")], 1));
        Assert.False(state.AutoFiles);

        state.SetQuery("report.d");
        state.SetFiles(new FileResults("report.d", [File("report.docx")], 1));

        Assert.True(state.AutoFiles);
    }

    [Fact]
    public void 自动切换后又有应用匹配_回到全部()
    {
        var state = NewState(App("微信"));
        state.SetQuery("w");
        state.SetFiles(new FileResults("w", [File("w.txt")], 1));
        state.SetQuery("wx");
        state.SetFiles(new FileResults("wx", [File("wx.txt")], 1));
        Assert.Equal(LauncherSource.All, state.EffectiveSource); // "wx" 匹配微信

        state.SetQuery("wxq");
        state.SetFiles(new FileResults("wxq", [File("wxq.txt")], 1));
        Assert.Equal(LauncherSource.Files, state.EffectiveSource);
    }

    [Fact]
    public void 手动点全部_切回来_这次输入不再自动切_清空后恢复()
    {
        var state = NewState(App("微信"));
        state.SetQuery("report");
        state.SetFiles(new FileResults("report", [File("report.docx")], 1));

        state.SetSource(LauncherSource.All);
        Assert.Equal(LauncherSource.All, state.EffectiveSource);
        Assert.Equal(LauncherItemKind.Web, state.Items[^1].Kind);

        state.SetQuery("report.");
        state.SetFiles(new FileResults("report.", [File("report.docx")], 1));
        Assert.Equal(LauncherSource.All, state.EffectiveSource);

        state.SetQuery("");
        state.SetQuery("report");
        state.SetFiles(new FileResults("report", [File("report.docx")], 1));
        Assert.Equal(LauncherSource.Files, state.EffectiveSource);
    }

    [Fact]
    public void 自动切换后按Tab_到文件夹_输入不变()
    {
        var state = NewState(App("微信"));
        state.SetQuery("report");
        state.SetFiles(new FileResults("report", [File("report.docx")], 1));

        var text = state.CycleSource(forward: true);

        Assert.Equal("report", text);
        Assert.Equal(LauncherSource.Folders, state.EffectiveSource);
    }

    [Fact]
    public void 关掉设置_不自动切()
    {
        var state = new LauncherState { Settings = new LauncherSettings { AutoSwitchToFiles = false } };
        state.SetQuery("report");

        state.SetFiles(new FileResults("report", [File("report.docx")], 1));

        Assert.Equal(LauncherSource.All, state.EffectiveSource);
    }

    [Fact]
    public async Task 自动切到文件后_按文件标签的300条重新搜()
    {
        var search = new CountingSearch(500);
        var state = new LauncherState();
        var presenter = NewPresenter(state, search);
        presenter.Show(LauncherSource.All);

        presenter.OnQueryChanged("report");
        await Settle(presenter);

        Assert.Equal([20, LauncherRanking.MaxFilesInSource], search.Limits);
        Assert.True(state.AutoFiles);
        Assert.Equal(LauncherRanking.MaxFilesInSource, state.Items.Count);
    }

    [Fact]
    public async Task 从全部手动切到文件标签_也按300条重新搜()
    {
        var search = new CountingSearch(500);
        var state = new LauncherState { Settings = new LauncherSettings { AutoSwitchToFiles = false } };
        var presenter = NewPresenter(state, search, new LauncherSettings { AutoSwitchToFiles = false });
        presenter.Show(LauncherSource.All);
        presenter.OnQueryChanged("report");
        await Settle(presenter);

        await presenter.SelectSourceAsync(LauncherSource.Files);
        await Settle(presenter);

        Assert.Equal([20, LauncherRanking.MaxFilesInSource], search.Limits); // 以前只搜了 20 条,"文件"里也只有 20 条
        Assert.Equal(LauncherRanking.MaxFilesInSource, state.Items.Count);
    }

    private static LauncherPresenter NewPresenter(LauncherState state, IFileSearch search, LauncherSettings? settings = null) => new(
        state, () => settings ?? new LauncherSettings(), new VisibleView(), new QuietSystem(), new RecordingLauncher(), new FakeClipboard(),
        new RecordingOverlay(), _ => { }, NullLogger.Instance, search);

    /// <summary>等文件搜索全部结束(结果到达后可能接着发起下一次)。</summary>
    private static async Task Settle(LauncherPresenter presenter)
    {
        for (var i = 0; i < 5; i++)
        {
            await presenter.PendingFileSearch;
        }
    }

    private sealed class CountingSearch(int total) : IFileSearch
    {
        public List<int> Limits { get; } = [];

        public FileResults Search(string text, bool foldersOnly, int limit, CancellationToken cancellation)
        {
            lock (Limits)
            {
                Limits.Add(limit);
            }

            var items = Enumerable.Range(0, Math.Min(limit, total)).Select(i => File($"{text}-{i}.txt")).ToList();
            return new FileResults(text, items, total);
        }
    }

    private sealed class VisibleView : ILauncherView
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

    private sealed class QuietSystem : IPanelSystem
    {
        public nint Foreground() => 1;

        public Task RestoreAsync(nint window) => Task.CompletedTask;

        public void AllowForegroundHandoff()
        {
        }

        public PanelPlacement Locate() => new(new ScreenRect(0, 0, 1920, 1080), 1);
    }
}
