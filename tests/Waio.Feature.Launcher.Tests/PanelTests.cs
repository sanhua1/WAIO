using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Panel;
using Waio.Testing;
using Waio.UI;
using static Waio.Feature.Launcher.Tests.RankingTests;

namespace Waio.Feature.Launcher.Tests;

/// <summary>面板状态、按键表、交互流程(打开 / 关闭 / 焦点 / 启动 / 复制)。</summary>
public class PanelTests
{
    private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private LauncherState NewState(params LauncherItem[] apps)
    {
        var state = new LauncherState { Clock = () => _now };
        state.SetApps(apps);
        return state;
    }

    // ---- 状态 ----

    [Fact]
    public void Esc_先清空输入_再回到全部_最后才关闭()
    {
        var state = NewState(App("微信"));
        state.SetSource(LauncherSource.Apps);
        state.SetQuery("wx");

        Assert.False(state.Escape());
        Assert.Equal("", state.Query);
        Assert.False(state.Escape());
        Assert.Equal(LauncherSource.All, state.SelectedSource);
        Assert.True(state.Escape());
    }

    [Fact]
    public void Tab切换来源_去掉输入里的前缀()
    {
        var state = NewState(App("微信"));
        state.SetQuery("a wx");
        Assert.Equal(LauncherSource.Apps, state.EffectiveSource);

        var text = state.CycleSource(forward: true);

        Assert.Equal("wx", text);
        Assert.Equal(LauncherSource.Files, state.EffectiveSource); // 全部 → 应用 → 文件 → 文件夹
        state.Settings = new LauncherSettings { FileSearchEnabled = false };
        state.CycleSource(forward: true);
        Assert.Equal(LauncherSource.Apps, state.EffectiveSource); // 关掉文件搜索只有全部 / 应用(文件不在列表里时从头算)
    }

    [Fact]
    public void 上下移动_到头回到另一头()
    {
        var state = NewState(App("Tool 1"), App("Tool 2"));
        state.Settings = new LauncherSettings { ShowWebFallback = false };
        state.SetQuery("tool");

        state.Move(-1);
        Assert.Equal(1, state.SelectedIndex);
        state.Move(1);
        Assert.Equal(0, state.SelectedIndex);
    }

    [Fact]
    public void 应用列表更新_保持选中项()
    {
        var state = NewState(App("Tool 1"), App("Tool 2"));
        state.SetQuery("tool");
        state.Select(1);

        state.SetApps([App("Tool 0"), App("Tool 1"), App("Tool 2")]);

        Assert.Equal("Tool 2", state.Selected?.Title);
    }

    [Fact]
    public void 输入为空的退格_只在不是全部时回到全部()
    {
        var state = NewState();
        Assert.False(state.BackspaceOnEmpty());
        state.SetSource(LauncherSource.Apps);
        Assert.True(state.BackspaceOnEmpty());
        Assert.Equal(LauncherSource.All, state.SelectedSource);
    }

    // ---- 按键 ----

    [Theory]
    [InlineData("Enter", false, "Open")]
    [InlineData("Enter", true, "Reveal")]
    [InlineData("N", true, "MoveDown")]
    [InlineData("P", true, "MoveUp")]
    [InlineData("C", true, "Copy")]
    [InlineData("Escape", false, "Escape")]
    [InlineData("N", false, "None")]
    public void 按键表(string key, bool ctrl, string expected) =>
        Assert.Equal(Enum.Parse<LauncherActionKind>(expected), LauncherKeys.Resolve(new(Enum.Parse<LauncherKey>(key), ctrl), new(false, false, false)).Kind);

    [Fact]
    public void 输入法组字时_所有按键交给输入法()
    {
        var composing = new LauncherKeyContext(Composing: true, HasTextSelection: false, QueryEmpty: false);

        Assert.Equal(LauncherActionKind.None, LauncherKeys.Resolve(new(LauncherKey.Enter), composing).Kind);
        Assert.Equal(LauncherActionKind.None, LauncherKeys.Resolve(new(LauncherKey.Escape), composing).Kind);
    }

    [Fact]
    public void 搜索框选中了文字_CtrlC是普通复制()
    {
        var selected = new LauncherKeyContext(false, HasTextSelection: true, QueryEmpty: false);

        Assert.Equal(LauncherActionKind.None, LauncherKeys.Resolve(new(LauncherKey.C, Ctrl: true), selected).Kind);
    }

    [Fact]
    public void Ctrl数字_快速打开_0不算()
    {
        var context = new LauncherKeyContext(false, false, false);

        Assert.Equal(new LauncherAction(LauncherActionKind.QuickOpen, 3), LauncherKeys.Resolve(new(LauncherKey.Digit, true, Digit: 3), context));
        Assert.Equal(LauncherActionKind.None, LauncherKeys.Resolve(new(LauncherKey.Digit, true, Digit: 0), context).Kind);
    }

    [Fact]
    public void 退格_只有输入为空时才处理()
    {
        Assert.Equal(LauncherActionKind.None, LauncherKeys.Resolve(new(LauncherKey.Backspace), new(false, false, QueryEmpty: false)).Kind);
        Assert.Equal(LauncherActionKind.BackspaceOnEmpty, LauncherKeys.Resolve(new(LauncherKey.Backspace), new(false, false, QueryEmpty: true)).Kind);
    }

    // ---- 交互流程 ----

    private sealed class Rig
    {
        public List<string> Log { get; } = [];

        public FakeView View { get; }

        public FakeSystem System { get; }

        public RecordingLauncher Launcher { get; } = new();

        public FakeClipboard Clipboard { get; } = new();

        public RecordingOverlay Overlay { get; } = new();

        public int Saves { get; private set; }

        public LauncherPresenter Presenter { get; }

        public LauncherState State { get; }

        public Rig(LauncherState state, LauncherSettings? settings = null)
        {
            State = state;
            View = new FakeView(Log);
            System = new FakeSystem(Log);
            var value = settings ?? new LauncherSettings();
            Presenter = new LauncherPresenter(
                state, () => value, View, System, Launcher, Clipboard, Overlay, _ => Saves++, NullLogger.Instance);
        }
    }

    [Fact]
    public async Task 快捷键_打开_再按关闭并把焦点还回去()
    {
        var rig = new Rig(NewState(App("微信")));

        await rig.Presenter.ToggleAsync(LauncherSource.All);
        Assert.True(rig.View.IsVisible);
        rig.View.IsActive = true;

        await rig.Presenter.ToggleAsync(LauncherSource.All);

        Assert.False(rig.View.IsVisible);
        Assert.Equal([FakeSystem.Previous], rig.System.Restored);
    }

    [Fact]
    public void 打开时带上设置里的面板大小百分比()
    {
        var rig = new Rig(NewState(), new LauncherSettings { PanelWidthPercent = 55, PanelHeightPercent = 45 });

        rig.Presenter.Show(LauncherSource.All);

        Assert.Equal((55, 45), (rig.View.LastPlacement!.WidthPercent, rig.View.LastPlacement.HeightPercent));
    }

    [Fact]
    public void 单击只选中不打开_键盘从这一项接着走()
    {
        var state = NewState(App("Tool 1"), App("Tool 2"), App("Tool 3"));
        var rig = new Rig(state, new LauncherSettings { ShowWebFallback = false });
        rig.Presenter.Show(LauncherSource.All);
        state.SetQuery("tool");

        rig.Presenter.SelectAt(2);

        Assert.Equal(2, state.SelectedIndex);
        Assert.Empty(rig.Launcher.Requests);
        Assert.True(rig.View.IsVisible);
    }

    [Fact]
    public async Task 显示着但不在前台_再按快捷键重新弹出而不是关闭()
    {
        var rig = new Rig(NewState());
        await rig.Presenter.ToggleAsync(LauncherSource.All);
        rig.View.IsActive = false;

        await rig.Presenter.ToggleAsync(LauncherSource.All);

        Assert.True(rig.View.IsVisible);
        Assert.Equal(2, rig.View.Shows);
    }

    [Fact]
    public async Task 打开应用_先放行前台再关面板_经ILauncher启动_不还焦点()
    {
        var rig = new Rig(NewState(App("记事本")));
        rig.Presenter.Show(LauncherSource.All);
        rig.Presenter.OnQueryChanged("jsb");

        await rig.Presenter.ExecuteAsync(new(LauncherActionKind.Open));

        Assert.Equal(["handoff", "hide"], rig.Log); // 面板还是前台时放行,之后才关
        Assert.Equal(@"shell:AppsFolder\{GUID}\记事本.exe", Assert.Single(rig.Launcher.Requests).Target);
        Assert.False(Assert.Single(rig.Launcher.Requests).RunElevated);
        Assert.Empty(rig.System.Restored);
        Assert.Equal(1, rig.Saves);
        Assert.True(rig.State.Usage.Usage(rig.State.Items[0].Id, _now) > 0);
    }

    [Fact]
    public async Task 启动失败_提示并把焦点还回去()
    {
        var rig = new Rig(NewState(App("微信")));
        rig.Launcher.FailWith = "找不到程序";
        rig.Presenter.Show(LauncherSource.All);
        rig.Presenter.OnQueryChanged("wx");

        await rig.Presenter.ExecuteAsync(new(LauncherActionKind.Open));

        Assert.Contains("找不到程序", Assert.Single(rig.Overlay.HudMessages));
        Assert.Equal([FakeSystem.Previous], rig.System.Restored);
    }

    [Fact]
    public async Task 打开文件位置_用资源管理器选中程序文件()
    {
        var dir = Directory.CreateTempSubdirectory("waio-launcher-");
        var exe = Path.Combine(dir.FullName, "tool.exe");
        File.WriteAllText(exe, "");
        var item = LauncherItem.App(new AppEntry("{GUID}\\tool.exe", "Tool", exe));
        var rig = new Rig(NewState(item));
        rig.Presenter.Show(LauncherSource.All);
        rig.Presenter.OnQueryChanged("tool");

        await rig.Presenter.ExecuteAsync(new(LauncherActionKind.Reveal));

        var request = Assert.Single(rig.Launcher.Requests);
        Assert.Equal("explorer", request.Target);
        Assert.Equal($"/select,\"{exe}\"", request.Arguments);
        dir.Delete(recursive: true);
    }

    [Fact]
    public async Task 程序文件已经不在了_提示并从列表拿掉_不启动()
    {
        var rig = new Rig(NewState(LauncherItem.App(new AppEntry("{GUID}\\gone.exe", "Gone", @"C:\不存在\gone.exe"))));
        rig.Presenter.Show(LauncherSource.All);
        rig.Presenter.OnQueryChanged("gone");

        await rig.Presenter.ExecuteAsync(new(LauncherActionKind.Open));

        Assert.Empty(rig.Launcher.Requests);
        Assert.Contains("已经不在了", Assert.Single(rig.Overlay.HudMessages));
        Assert.Empty(rig.State.Apps);
    }

    [Fact]
    public async Task 复制_网页搜索复制网址_面板不关()
    {
        var rig = new Rig(NewState());
        rig.Presenter.Show(LauncherSource.All);
        rig.Presenter.OnQueryChanged("bd 天气");

        await rig.Presenter.ExecuteAsync(new(LauncherActionKind.Copy));

        Assert.Equal("https://www.baidu.com/s?wd=%E5%A4%A9%E6%B0%94", Assert.Single(rig.Clipboard.Written).Text);
        Assert.True(rig.View.IsVisible);
    }

    [Fact]
    public async Task 关闭后几秒内再打开_保留输入_超时就清空()
    {
        var rig = new Rig(NewState(App("微信")));
        rig.Presenter.Show(LauncherSource.All);
        rig.Presenter.OnQueryChanged("wx");
        await rig.Presenter.CloseAsync(restoreFocus: false);

        _now += TimeSpan.FromSeconds(3);
        rig.Presenter.Show(LauncherSource.All);
        Assert.Equal("wx", rig.View.Query);
        await rig.Presenter.CloseAsync(restoreFocus: false);

        _now += TimeSpan.FromSeconds(10);
        rig.Presenter.Show(LauncherSource.All);
        Assert.Equal("", rig.View.Query);
    }

    [Fact]
    public void 只搜应用的命令_打开就在应用来源()
    {
        var rig = new Rig(NewState(App("微信")));

        rig.Presenter.Show(LauncherSource.Apps);

        Assert.Equal(LauncherSource.Apps, rig.State.EffectiveSource);
    }

    private sealed class FakeView(List<string> log) : ILauncherView
    {
        public bool IsVisible { get; private set; }

        public bool IsActive { get; set; }

        public string Query { get; private set; } = "";

        public int Shows { get; private set; }

        public void Render(LauncherState state)
        {
        }

        public void SetQuery(string text) => Query = text;

        public PanelPlacement? LastPlacement { get; private set; }

        public void Show(PanelPlacement placement)
        {
            LastPlacement = placement;
            IsVisible = true;
            IsActive = true;
            Shows++;
        }

        public void Hide()
        {
            IsVisible = false;
            IsActive = false;
            log.Add("hide");
        }
    }

    private sealed class FakeSystem(List<string> log) : IPanelSystem
    {
        public const nint Previous = 42;

        public List<nint> Restored { get; } = [];

        public nint Foreground() => Previous;

        public Task RestoreAsync(nint window)
        {
            Restored.Add(window);
            return Task.CompletedTask;
        }

        public void AllowForegroundHandoff()
        {
            log.Add("handoff");
        }

        public PanelPlacement Locate() => new(new ScreenRect(0, 0, 1920, 1080), 1.0);
    }
}
