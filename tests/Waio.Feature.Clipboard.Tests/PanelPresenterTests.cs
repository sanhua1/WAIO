using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Clipboard;
using Waio.Feature.Clipboard.Domain;
using Waio.Feature.Clipboard.Infrastructure;
using Waio.Feature.Clipboard.Panel;
using Waio.Kit.Input;
using Waio.Kit.Services;
using Waio.Testing;
using Waio.UI;

namespace Waio.Feature.Clipboard.Tests;

public sealed class PanelPresenterTests : IDisposable
{
    /// <summary>记录所有调用(含顺序),并模拟"面板是否可见"。</summary>
    private sealed class FakeView : IPanelView
    {
        public List<string> Log { get; } = [];

        public bool IsVisible { get; private set; }

        /// <summary>模拟"显示着但没抢到前台"。</summary>
        public bool ActivationStolen { get; set; }

        public bool IsActive => IsVisible && !ActivationStolen;

        public string[] Rendered { get; private set; } = [];

        public int SelectedIndex { get; private set; }

        public string Query { get; private set; } = "";

        public ClipboardContent? PreviewContent { get; private set; }

        public bool PreviewOpen { get; private set; }

        public PanelPlacement? LastPlacement { get; private set; }

        public bool? FocusSearchOnShow { get; private set; }

        public void Render(PickerState state)
        {
            Rendered = [.. state.Items.Select(i => i.Summary)];
            SelectedIndex = state.SelectedIndex;
        }

        public void ShowPreview(ClipItem item, ClipboardContent? content)
        {
            PreviewOpen = true;
            PreviewContent = content;
        }

        public void ClosePreview() => PreviewOpen = false;

        public void SetQuery(string text) => Query = text;

        public void FocusSearch() => Log.Add("focus-search");

        public void FocusList() => Log.Add("focus-list");

        public void Show(PanelPlacement placement, bool focusSearch)
        {
            IsVisible = true;
            LastPlacement = placement;
            FocusSearchOnShow = focusSearch;
            Log.Add("show");
        }

        public void Hide()
        {
            IsVisible = false;
            Log.Add("hide");
        }
    }

    private sealed class FakeFocus(List<string> log) : IWindowFocus
    {
        public nint ForegroundWindow { get; set; } = 1234;

        public bool RestoreSucceeds { get; set; } = true;

        public nint? Restored { get; private set; }

        public nint Foreground() => ForegroundWindow;

        public Task<bool> RestoreAsync(nint window)
        {
            Restored = window;
            log.Add("restore-focus");
            return Task.FromResult(RestoreSucceeds);
        }
    }

    private sealed class FakePlacement : IPlacementProvider
    {
        public PanelPlacement Result { get; set; } = new(null, new ScreenRect(0, 0, 1920, 1080), 1.0);

        public nint? LastForeground { get; private set; }

        public Task<PanelPlacement> LocateAsync(nint foreground, ClipboardSettings settings)
        {
            LastForeground = foreground;
            return Task.FromResult(Result);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waio-clip-panel-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private readonly ClipboardSettings _settings = new();
    private readonly SqliteClipStore _store;
    private readonly ClipHistory _history;
    private readonly FakeFeatureContext _ctx = new("clipboard");
    private readonly FakeView _view = new();
    private readonly FakeFocus _focus;
    private readonly FakePlacement _placement = new();
    private readonly PanelPresenter _presenter;

    public PanelPresenterTests()
    {
        _store = SqliteClipStore.Open(_dir, NullLogger.Instance);
        _history = new ClipHistory(_store, () => _settings, _clock);
        _focus = new FakeFocus(_view.Log);
        _presenter = new PanelPresenter(
            _history, () => _settings, _ctx.FakeClipboard, _ctx.Input, _ctx.Overlay, _view, _focus, _placement, NullLogger.Instance);
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

    private long Add(string text, string app = "notepad")
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        return _history.Record(new ClipboardChange(ClipboardContent.FromText(text), app)).Id!.Value;
    }

    private ClipItem Item(long id) => _history.Load().Single(i => i.Id == id);

    private static PanelKeyInput Key(PanelKey key, bool ctrl = false, bool shift = false) => new(key, ctrl, shift);

    private async Task Press(PanelKey key, bool ctrl = false, bool shift = false, bool repeat = false)
    {
        var action = _presenter.Resolve(new PanelKeyInput(key, ctrl, shift, Repeat: repeat));
        await _presenter.ExecuteAsync(action);
    }

    private async Task Release(PanelKey key) =>
        await _presenter.ExecuteAsync(_presenter.ResolveKeyUp(Key(key)));

    // ---- 显示 ----

    [Fact]
    public async Task 显示_列表最近的在前_默认选中第一条()
    {
        Add("old");
        Add("new");

        await _presenter.ShowAsync();

        Assert.True(_view.IsVisible);
        Assert.Equal(["new", "old"], _view.Rendered);
        Assert.Equal(0, _view.SelectedIndex);
    }

    [Fact]
    public async Task 显示_记住弹出前的前台窗口_并按它定位()
    {
        _focus.ForegroundWindow = 777;

        await _presenter.ShowAsync();

        Assert.Equal((nint)777, _placement.LastForeground);
        Assert.Same(_placement.Result, _view.LastPlacement);
    }

    [Fact]
    public async Task 显示_explicit模式搜索框不抢焦点_typeToSearch模式搜索框有焦点()
    {
        await _presenter.ShowAsync();
        Assert.False(_view.FocusSearchOnShow);

        _settings.SearchMode = SearchMode.TypeToSearch;
        await _presenter.ShowAsync();
        Assert.True(_view.FocusSearchOnShow);
    }

    [Fact]
    public async Task 再次触发_已显示则关闭()
    {
        await _presenter.ToggleAsync();
        Assert.True(_view.IsVisible);

        await _presenter.ToggleAsync();

        Assert.False(_view.IsVisible);
    }

    [Fact]
    public async Task 再次触发_显示着但没在前台_重新弹出而不是关闭()
    {
        await _presenter.ToggleAsync();
        _view.ActivationStolen = true; // 例如被别的置顶程序抢了前台
        _view.Log.Clear();

        await _presenter.ToggleAsync();

        Assert.True(_view.IsVisible);
        Assert.Equal(["hide", "show"], _view.Log.Where(l => l is "hide" or "show"));
    }

    // ---- 焦点:列表 / 搜索框 ----

    [Fact]
    public async Task 默认焦点在列表_Ctrl_F进搜索框_搜索框里按下_回到列表并选中第一条()
    {
        Add("a");
        Add("b");
        Add("c");
        await _presenter.ShowAsync();
        Assert.False(_view.FocusSearchOnShow);

        await Press(PanelKey.Down);
        await Press(PanelKey.Down);
        Assert.Equal(2, _view.SelectedIndex);

        await Press(PanelKey.F, ctrl: true);
        Assert.Equal("focus-search", _view.Log[^1]);

        await Press(PanelKey.Down);
        Assert.Equal("focus-list", _view.Log[^1]);
        Assert.Equal(0, _view.SelectedIndex);

        await Press(PanelKey.Down); // 回到列表后上下键照常移动
        Assert.Equal(1, _view.SelectedIndex);
    }

    // ---- 选择 ----

    [Fact]
    public async Task 上下键移动选中_到头循环()
    {
        Add("a");
        Add("b");
        Add("c");
        await _presenter.ShowAsync();

        await Press(PanelKey.Down);
        Assert.Equal(1, _view.SelectedIndex);
        await Press(PanelKey.Up);
        await Press(PanelKey.Up);
        Assert.Equal(2, _view.SelectedIndex);
    }

    // ---- 搜索 ----

    [Fact]
    public async Task 输入搜索词_列表只剩匹配的()
    {
        Add("apple pie");
        Add("banana");
        Add("Apple juice");
        await _presenter.ShowAsync();

        await _presenter.OnQueryChangedAsync("APPLE");

        Assert.Equal(["Apple juice", "apple pie"], _view.Rendered);
    }

    [Fact]
    public async Task explicit_Ctrl_F开始搜索_回车结束搜索保留结果_再回车才粘贴()
    {
        Add("alpha");
        Add("beta");
        await _presenter.ShowAsync();

        await Press(PanelKey.F, ctrl: true);
        await _presenter.OnQueryChangedAsync("alpha");
        await Press(PanelKey.Enter); // 搜索中:只是结束搜索

        Assert.Equal(["alpha"], _view.Rendered);
        Assert.Empty(_ctx.FakeClipboard.Written);
        Assert.Contains("focus-list", _view.Log);

        await Press(PanelKey.Enter); // 现在才粘贴
        Assert.Single(_ctx.FakeClipboard.Written);
    }

    [Fact]
    public async Task explicit_搜索中Esc_清空搜索并恢复完整列表()
    {
        Add("alpha");
        Add("beta");
        await _presenter.ShowAsync();
        await Press(PanelKey.F, ctrl: true);
        await _presenter.OnQueryChangedAsync("alpha");

        await Press(PanelKey.Escape);

        Assert.Equal("", _view.Query);
        Assert.Equal(2, _view.Rendered.Length);
        Assert.True(_view.IsVisible); // 只是清空搜索,面板还开着
    }

    [Fact]
    public async Task Esc逐层退出_预览_搜索_面板()
    {
        Add("alpha");
        await _presenter.ShowAsync();
        await Press(PanelKey.F, ctrl: true);
        await _presenter.OnQueryChangedAsync("alp");
        await Press(PanelKey.Enter); // 结束搜索,焦点回列表
        await Press(PanelKey.Space); // 预览
        Assert.True(_view.PreviewOpen);

        await Press(PanelKey.Escape);
        Assert.False(_view.PreviewOpen);
        Assert.True(_view.IsVisible);

        await Press(PanelKey.Escape);
        Assert.Equal("", _view.Query);
        Assert.True(_view.IsVisible);

        await Press(PanelKey.Escape);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public async Task 输入法组字期间_回车不会粘贴()
    {
        Add("alpha");
        _settings.SearchMode = SearchMode.TypeToSearch;
        await _presenter.ShowAsync();
        _presenter.SetComposing(true);

        await Press(PanelKey.Enter);

        Assert.Empty(_ctx.FakeClipboard.Written);
        Assert.True(_view.IsVisible);
    }

    [Fact]
    public async Task 较旧的搜索结果不会覆盖较新的()
    {
        Add("alpha");
        Add("beta");
        await _presenter.ShowAsync();

        var slow = _presenter.OnQueryChangedAsync("alpha");
        var fast = _presenter.OnQueryChangedAsync("beta");
        await Task.WhenAll(slow, fast);

        Assert.Equal(["beta"], _view.Rendered);
    }

    // ---- 粘贴 ----

    [Fact]
    public async Task 回车粘贴_顺序是_隐藏面板_写剪贴板_还原焦点_发Ctrl_V()
    {
        Add("hello");
        _focus.ForegroundWindow = 4321;
        await _presenter.ShowAsync();
        _view.Log.Clear();

        await Press(PanelKey.Enter);

        Assert.Equal(["hide", "restore-focus"], _view.Log);
        Assert.Equal("hello", Assert.Single(_ctx.FakeClipboard.Written).Text);
        Assert.Equal((nint)4321, _focus.Restored);
        Assert.Equal([KeyChord.Parse("ctrl+v")], _ctx.FakeInput.SentKeys);
    }

    [Fact]
    public async Task 粘贴_把条目移到最前()
    {
        var old = Add("old");
        Add("new");
        await _presenter.ShowAsync();
        await Press(PanelKey.Down); // 选中 old

        await Press(PanelKey.Enter);

        Assert.Equal(["old", "new"], _history.Load().OrderByDescending(i => i.LastUsedAt).Select(i => i.Summary));
        Assert.Equal(old, _history.Load().OrderByDescending(i => i.LastUsedAt).First().Id);
    }

    [Fact]
    public async Task 关闭粘贴后置顶_顺序不变()
    {
        _settings.MoveToTopOnPaste = false;
        Add("old");
        Add("new");
        await _presenter.ShowAsync();
        await Press(PanelKey.Down);

        await Press(PanelKey.Enter);

        Assert.Equal("new", _history.Load().OrderByDescending(i => i.LastUsedAt).First().Summary);
    }

    [Fact]
    public async Task Shift回车_粘贴纯文本_去掉富文本格式()
    {
        _history.Record(new ClipboardChange(new ClipboardContent { Text = "rich", Rtf = [1, 2], Html = [3] }, "word"));
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter, shift: true);

        var written = Assert.Single(_ctx.FakeClipboard.Written);
        Assert.Equal("rich", written.Text);
        Assert.Null(written.Rtf);
        Assert.Null(written.Html);
    }

    [Fact]
    public async Task 普通回车_保留所有格式()
    {
        _history.Record(new ClipboardChange(new ClipboardContent { Text = "rich", Rtf = [1, 2] }, "word"));
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter);

        Assert.NotNull(Assert.Single(_ctx.FakeClipboard.Written).Rtf);
    }

    [Fact]
    public async Task 纯文本粘贴文件列表_退化成换行分隔的路径()
    {
        _history.Record(new ClipboardChange(ClipboardContent.FromFiles([@"C:\a.txt", @"D:\b.txt"]), "explorer"));
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter, shift: true);

        Assert.Equal("C:\\a.txt\r\nD:\\b.txt", Assert.Single(_ctx.FakeClipboard.Written).Text);
    }

    [Fact]
    public async Task 纯文本粘贴纯图片_提示没有文字_不写剪贴板()
    {
        _history.Record(new ClipboardChange(new ClipboardContent { Image = [1, 2, 3] }, "paint"));
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter, shift: true);

        Assert.Empty(_ctx.FakeClipboard.Written);
        Assert.Contains("没有文字", Assert.Single(_ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task Ctrl数字_快速粘贴对应条目()
    {
        Add("one");
        Add("two");
        Add("three");
        await _presenter.ShowAsync();

        await Press(PanelKey.Digit, ctrl: true, shift: false); // Digit 缺省值 -1 不应触发
        Assert.Empty(_ctx.FakeClipboard.Written);

        await _presenter.ExecuteAsync(PanelKeys.Resolve(
            new PanelKeyInput(PanelKey.Digit, Ctrl: true, Digit: 2),
            new PanelContext(SearchMode.Explicit, false, false, false, true)));

        Assert.Equal("two", Assert.Single(_ctx.FakeClipboard.Written).Text);
    }

    [Fact]
    public async Task 不直接粘贴_只复制到剪贴板并提示()
    {
        _settings.PasteDirectly = false;
        Add("hello");
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter);

        Assert.Single(_ctx.FakeClipboard.Written);
        Assert.Empty(_ctx.FakeInput.SentKeys);
        Assert.Null(_focus.Restored);
        Assert.Contains("已复制", Assert.Single(_ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task 无法切回原窗口_不发Ctrl_V_提示用户手动粘贴()
    {
        _focus.RestoreSucceeds = false;
        Add("hello");
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter);

        Assert.Empty(_ctx.FakeInput.SentKeys);
        Assert.Contains("手动粘贴", Assert.Single(_ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task 没有原窗口_不发Ctrl_V()
    {
        _focus.ForegroundWindow = 0;
        Add("hello");
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter);

        Assert.Empty(_ctx.FakeInput.SentKeys);
    }

    [Fact]
    public async Task 剪贴板被占用_提示失败_不发Ctrl_V()
    {
        _ctx.FakeClipboard.CanWrite = false;
        Add("hello");
        await _presenter.ShowAsync();

        await Press(PanelKey.Enter);

        Assert.Empty(_ctx.FakeInput.SentKeys);
        Assert.Contains("占用", Assert.Single(_ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task 条目内容已丢失_提示_不写剪贴板()
    {
        var id = Add("hello");
        await _presenter.ShowAsync();
        _store.Delete([id]); // 绕过 presenter 直接删,模拟数据已不在

        await Press(PanelKey.Enter);

        Assert.Empty(_ctx.FakeClipboard.Written);
        Assert.Contains("丢失", Assert.Single(_ctx.FakeOverlay.HudMessages));
    }

    [Fact]
    public async Task 点击条目_等同回车粘贴()
    {
        var id = Add("clicked");
        await _presenter.ShowAsync();

        await _presenter.PasteAsync(Item(id), plain: false);

        Assert.Equal("clicked", Assert.Single(_ctx.FakeClipboard.Written).Text);
    }

    [Fact]
    public async Task 仅复制_写剪贴板但不粘贴()
    {
        var id = Add("copy me");
        await _presenter.ShowAsync();

        await _presenter.CopyOnlyAsync(Item(id));

        Assert.Single(_ctx.FakeClipboard.Written);
        Assert.Empty(_ctx.FakeInput.SentKeys);
    }

    // ---- 删除、固定、预览 ----

    [Fact]
    public async Task 删除键_删掉选中条目_列表更新()
    {
        Add("keep");
        Add("drop");
        await _presenter.ShowAsync();

        await Press(PanelKey.Delete);

        Assert.Equal(["keep"], _view.Rendered);
        Assert.Single(_history.Load());
    }

    [Fact]
    public async Task Ctrl_P固定_固定的排到最后_再按取消固定()
    {
        var first = Add("first");
        Add("second");
        await _presenter.ShowAsync();
        await Press(PanelKey.Down); // 选中 first

        await Press(PanelKey.P, ctrl: true);
        Assert.Equal(["second", "first"], _view.Rendered);
        Assert.True(Item(first).IsPinned);

        Assert.Equal(first, _presenter.State.Selected!.Id); // 固定后选中项仍是 first(在最后一行)
        await Press(PanelKey.P, ctrl: true);
        Assert.False(Item(first).IsPinned);
    }

    [Fact]
    public async Task 空格预览_加载完整内容_再按空格关闭()
    {
        Add("preview me");
        await _presenter.ShowAsync();

        await Press(PanelKey.Space);
        Assert.True(_view.PreviewOpen);
        Assert.Equal("preview me", _view.PreviewContent?.Text);

        await Press(PanelKey.Space);
        Assert.False(_view.PreviewOpen);
    }

    [Fact]
    public async Task 按住空格不放_自动重复不会反复开关预览()
    {
        Add("preview me");
        await _presenter.ShowAsync();

        await Press(PanelKey.Space);
        await Press(PanelKey.Space, repeat: true);
        await Press(PanelKey.Space, repeat: true);

        Assert.True(_view.PreviewOpen);
    }

    [Fact]
    public async Task 按住预览模式_按下打开_松开关闭()
    {
        _settings.PreviewMode = PreviewMode.Hold;
        Add("preview me");
        await _presenter.ShowAsync();

        await Press(PanelKey.Space);
        Assert.True(_view.PreviewOpen);
        await Press(PanelKey.Space, repeat: true);
        Assert.True(_view.PreviewOpen);

        await Release(PanelKey.Space);
        Assert.False(_view.PreviewOpen);
    }

    [Fact]
    public async Task 切换模式_松开空格不关闭预览()
    {
        Add("preview me");
        await _presenter.ShowAsync();

        await Press(PanelKey.Space);
        await Release(PanelKey.Space);

        Assert.True(_view.PreviewOpen);
    }

    [Fact]
    public async Task 预览开着时上下移动_预览跟着换()
    {
        Add("first");
        Add("second");
        await _presenter.ShowAsync();
        await Press(PanelKey.Space);
        Assert.Equal("second", _view.PreviewContent?.Text);

        await Press(PanelKey.Down);

        Assert.True(_view.PreviewOpen);
        Assert.Equal("first", _view.PreviewContent?.Text);
    }

    [Fact]
    public async Task 图片条目_列表标记有图_能单独读出图片数据()
    {
        var id = _history.Record(new ClipboardChange(
            new ClipboardContent { Files = [@"C:\shot.png"], Image = [1, 2, 3] }, "PixPin")).Id!.Value;
        Add("text only");

        Assert.True(Item(id).HasImage);
        Assert.False(_history.Load().Single(i => i.Summary == "text only").HasImage);
        Assert.Equal([1, 2, 3], await _presenter.LoadImageAsync(id));
    }

    [Fact]
    public async Task 删除正在预览的条目_预览关闭()
    {
        var id = Add("gone");
        await _presenter.ShowAsync();
        await Press(PanelKey.Space);

        await _presenter.DeleteAsync(id);

        Assert.False(_view.PreviewOpen);
    }

    // ---- 新内容到达 ----

    [Fact]
    public async Task 面板开着时来了新复制_列表刷新_选中项不变()
    {
        Add("a");
        Add("b");
        await _presenter.ShowAsync();
        await Press(PanelKey.Down);   // 选中 a
        var before = _presenter.State.Selected!.Id;

        Add("c");
        await _presenter.NotifyHistoryChangedAsync();

        Assert.Equal(["c", "b", "a"], _view.Rendered);
        Assert.Equal(before, _presenter.State.Selected!.Id);
    }

    [Fact]
    public async Task 面板没开时来了新复制_不刷新视图()
    {
        Add("a");
        await _presenter.ShowAsync();
        _presenter.Hide();
        var rendered = _view.Rendered;
        Add("b");

        await _presenter.NotifyHistoryChangedAsync();

        Assert.Same(rendered, _view.Rendered);
    }
}

