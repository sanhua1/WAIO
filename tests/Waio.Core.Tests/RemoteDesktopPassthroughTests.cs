using Waio.Core.Input;
using Waio.Core.Storage;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

/// <summary>远程桌面程序在前台时让出快捷键和改键(ADR-0033)。</summary>
public class RemoteDesktopPassthroughTests
{
    private sealed class Sender : IKeySender
    {
        public List<(int Vk, bool Down)> Keys { get; } = [];

        public void Send(KeyChord chord)
        {
        }

        public void SendKey(int vk, bool down) => Keys.Add((vk, down));

        public void TapMaskKey()
        {
        }
    }

    private sealed class Probe : IKeyStateProbe
    {
        public HashSet<int> Down { get; } = [];

        public bool IsDown(int vk) => Down.Contains(vk);
    }

    /// <summary>吞掉 x;记录 OnReset 次数。</summary>
    private sealed class SwallowX : IRealtimeKeyFilter
    {
        public int Resets { get; private set; }

        public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter) =>
            keyEvent.Key == "x" ? FilterDecision.Consume : FilterDecision.Pass;

        public void OnReset(IKeyEmitter emitter) => Resets++;
    }

    private readonly Sender _sender = new();
    private readonly Probe _probe = new();
    private readonly List<CommandBinding> _dispatched = [];
    private readonly SwallowX _filter = new();
    private readonly InputPipeline _pipeline;
    private uint _clock = 1000;

    private string? Foreground { get; set; } = "notepad";

    public RemoteDesktopPassthroughTests()
    {
        _pipeline = new InputPipeline(_sender, _probe, _dispatched.Add, () => Foreground, _ => { });
        _pipeline.SetMatcher(BindingMatcher.Build(
        [
            new CommandBinding(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse("alt+f1")), "screenshot.capture",
                CommandArguments.Empty, BindingScope.Global, true),
        ]));
        _pipeline.AddFilter(_filter);
        _pipeline.SetPassthroughApps(["GameViewer"]);
    }

    private bool Key(string name, bool down)
    {
        Assert.True(KeyNames.TryGetVk(name, out var vk));
        _clock += 30;
        var consumed = _pipeline.Process(new RawKey(vk, down, false, _clock));
        if (down && !consumed)
        {
            _probe.Down.Add(vk);
        }
        else if (!down)
        {
            _probe.Down.Remove(vk);
        }

        return consumed;
    }

    [Fact]
    public void 远程桌面在前台_快捷键和过滤器都放行()
    {
        Foreground = "gameviewer"; // 进程名不区分大小写

        Key("lalt", true);
        Assert.False(Key("f1", true));
        Assert.False(Key("f1", false));
        Key("lalt", false);
        Assert.False(Key("x", true));
        Assert.False(Key("x", false));

        Assert.Empty(_dispatched);
    }

    [Fact]
    public void 其他程序在前台_照常触发快捷键和过滤器()
    {
        Key("lalt", true);
        Assert.True(Key("f1", true));
        Assert.True(Key("f1", false));
        Key("lalt", false);
        Assert.True(Key("x", true));

        Assert.Single(_dispatched);
    }

    [Fact]
    public void 切到远程之前吞掉的键_长按重复和抬起照样吞()
    {
        Assert.True(Key("x", true));
        Foreground = "GameViewer";

        Assert.True(Key("x", true)); // 自动重复
        Assert.True(Key("x", false)); // 不能让远程软件收到孤立的抬起
        Assert.False(Key("x", true)); // 之后的新按下放行
    }

    [Fact]
    public void 刚进入远程_过滤器只重置一次_离开再进入再重置()
    {
        Foreground = "GameViewer";
        Key("a", true);
        Key("a", false);
        Assert.Equal(1, _filter.Resets);

        Foreground = "notepad";
        Key("a", true);
        Key("a", false);
        Foreground = "GameViewer";
        Key("a", true);
        Assert.Equal(2, _filter.Resets);
    }

    [Fact]
    public void 名单为空_不让出()
    {
        _pipeline.SetPassthroughApps([]);
        Foreground = "GameViewer";

        Assert.True(Key("x", true));
        Assert.Equal(0, _filter.Resets);
    }

    [Fact]
    public void 键盘会话期间_按键仍归会话()
    {
        Foreground = "GameViewer";
        _ = _pipeline.BeginSession(new KeyboardSessionOptions(ConsumeEvents: true));

        Assert.True(Key("a", true));
    }

    [Fact]
    public void 按住自定义修饰键时切到远程_作废且不补发()
    {
        _pipeline.SetCustomModifiers(["f24"]);
        Assert.True(Key("f24", true));
        Foreground = "GameViewer";

        Assert.True(Key("f24", false)); // 按下被吞过,抬起也吞
        Assert.Empty(_sender.Keys); // 不把 F24 补发给远程软件
        Assert.False(Key("f24", true)); // 远程里的 F24 原样放行
    }

    [Fact]
    public void 设置_默认开启且包含UU远程_关闭时名单为空()
    {
        var settings = new RemoteDesktopSettings();
        Assert.True(settings.Enabled);
        Assert.Contains("GameViewer", settings.ActiveApps());

        settings.Enabled = false;
        Assert.Empty(settings.ActiveApps());
    }

    [Fact]
    public void 设置_进程名规范化_去后缀去空白去重_丢掉路径()
    {
        var names = RemoteDesktopSettings.Normalize([" GameViewer.exe ", "gameviewer", "", @"C:\x\a.exe", "ToDesk.EXE", "  "]);

        Assert.Equal(["GameViewer", "ToDesk"], names);
    }

    [Fact]
    public void 设置_旧版app_json没有这一项时用默认值_写过的名单原样读回()
    {
        using var root = new TempRoot();
        var paths = root.Paths;
        var store = new AppSettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AppSettingsFile)!);
        File.WriteAllText(paths.AppSettingsFile, """{ "schemaVersion": 1, "features": {} }""");

        var loaded = store.Load();
        Assert.True(loaded.RemoteDesktop.Enabled);
        Assert.Equal(RemoteDesktopSettings.DefaultApps, loaded.RemoteDesktop.Apps);

        File.WriteAllText(paths.AppSettingsFile, """{ "remoteDesktop": { "enabled": false, "apps": ["MyVm"] } }""");
        loaded = store.Load();
        Assert.False(loaded.RemoteDesktop.Enabled);
        Assert.Equal(["MyVm"], loaded.RemoteDesktop.Apps);
    }
}
