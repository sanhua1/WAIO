using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

public class InputPipelineTests
{
    private sealed class FakeSender : IKeySender
    {
        public List<KeyChord> Sent { get; } = [];

        public int MaskTaps { get; private set; }

        public void Send(KeyChord chord) => Sent.Add(chord);

        public List<(int Vk, bool Down)> SentKeyEvents { get; } = [];

        public void SendKey(int vk, bool down) => SentKeyEvents.Add((vk, down));

        public void TapMaskKey() => MaskTaps++;
    }

    /// <summary>模拟"物理键盘"的真实状态:系统在钩子返回之后才更新按键状态,所以先 Process 再更新。</summary>
    private sealed class FakeKeyboard : IKeyStateProbe
    {
        public HashSet<int> Physical { get; } = [];

        public bool IsDown(int vk) => Physical.Contains(vk);
    }

    private sealed class Harness
    {
        public FakeSender Sender { get; } = new();

        public FakeKeyboard Keyboard { get; } = new();

        public List<CommandBinding> Dispatched { get; } = [];

        public List<Exception> Errors { get; } = [];

        public string? Foreground { get; set; } = "notepad";

        public InputPipeline Pipeline { get; }

        public Harness(params CommandBinding[] bindings)
        {
            Pipeline = new InputPipeline(Sender, Keyboard, Dispatched.Add, () => Foreground, Errors.Add);
            Pipeline.SetMatcher(BindingMatcher.Build(bindings));
        }

        /// <summary>事件时间戳(毫秒);每个事件自动前进 30 ms(像真实的自动重复间隔),<see cref="Wait"/> 可以拉开间隔。</summary>
        public uint Clock { get; private set; } = 1000;

        public void Wait(uint ms) => Clock += ms;

        /// <summary>
        /// 和真实系统一致:被钩子吞掉的按下<b>不会</b>更新系统的按键状态(GetAsyncKeyState 仍报告松开)。
        /// 早先这里不管吞没吞都记为按下,掩盖了"拿系统状态核对被吞的 F24"的 bug。
        /// </summary>
        public bool Down(string key)
        {
            Clock += 30;
            var consumed = Pipeline.Process(new RawKey(Vk(key), true, false, Clock));
            if (!consumed)
            {
                Keyboard.Physical.Add(Vk(key));
            }

            return consumed;
        }

        public bool Up(string key)
        {
            Clock += 30;
            var consumed = Pipeline.Process(new RawKey(Vk(key), false, false, Clock));
            Keyboard.Physical.Remove(Vk(key));
            return consumed;
        }

        /// <summary>模拟系统丢了这个键的"抬起"事件:物理上已松开,但钩子没收到。</summary>
        public void LoseUp(string key) => Keyboard.Physical.Remove(Vk(key));

        public bool Ours(string key, bool down) => Pipeline.Process(new RawKey(Vk(key), down, true));

        private static int Vk(string key) =>
            KeyNames.TryGetVk(key, out var vk) ? vk : throw new ArgumentException(key);
    }

    private static CommandBinding Bind(string chord, string command = "test.run", BindingScope? scope = null, bool enabled = true) =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, CommandArguments.Empty, scope ?? BindingScope.Global, enabled);

    [Fact]
    public void 命中快捷键_吞按下_分发一次_也吞掉对应的抬起()
    {
        var h = new Harness(Bind("ctrl+alt+v"));

        h.Down("lctrl");
        h.Down("lalt");
        Assert.True(h.Down("v"));          // 按下被吞
        Assert.True(h.Up("v"));            // 抬起也要吞,否则目标程序收到孤儿抬起
        Assert.False(h.Up("lalt"));        // 修饰键的抬起必须放行,否则目标以为 Alt 一直按着
        Assert.False(h.Up("lctrl"));

        Assert.Single(h.Dispatched);
    }

    [Fact]
    public void 没有命中_全部放行_不分发()
    {
        var h = new Harness(Bind("ctrl+alt+v"));

        h.Down("lctrl");
        Assert.False(h.Down("v"));
        Assert.False(h.Up("v"));

        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public void 长按产生的重复按下_只分发一次且被吞()
    {
        var h = new Harness(Bind("ctrl+v"));
        h.Down("lctrl");

        Assert.True(h.Down("v"));
        Assert.True(h.Down("v")); // 自动重复
        Assert.True(h.Down("v"));
        h.Up("v");

        Assert.Single(h.Dispatched);
    }

    [Fact]
    public void 命中带Win或Alt的快捷键_发一次掩码键打断开始菜单和菜单栏()
    {
        var withWin = new Harness(Bind("win+shift+n"));
        withWin.Down("lwin");
        withWin.Down("lshift");
        withWin.Down("n");
        Assert.Equal(1, withWin.Sender.MaskTaps);

        var withAlt = new Harness(Bind("alt+q"));
        withAlt.Down("lalt");
        withAlt.Down("q");
        Assert.Equal(1, withAlt.Sender.MaskTaps);

        var ctrlOnly = new Harness(Bind("ctrl+q"));
        ctrlOnly.Down("lctrl");
        ctrlOnly.Down("q");
        Assert.Equal(0, ctrlOnly.Sender.MaskTaps);
    }

    [Fact]
    public void WAIO自己发出的事件_原样放行_不参与匹配()
    {
        var h = new Harness(Bind("v"));

        Assert.False(h.Ours("v", down: true));
        Assert.False(h.Ours("v", down: false));

        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public void 不认识的键_放行()
    {
        var h = new Harness(Bind("v"));

        Assert.False(h.Pipeline.Process(new RawKey(0xFE, true, false))); // VK_OEM_CLEAR,未收录
    }

    [Fact]
    public void 修饰键状态随按下抬起正确更新()
    {
        var h = new Harness(Bind("ctrl+v"));

        h.Down("lctrl");
        h.Up("lctrl");

        Assert.False(h.Down("v")); // ctrl 已松开,不应命中
        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public void 左右修饰键等价_且一个抬起另一个仍按住时仍算按下()
    {
        var h = new Harness(Bind("ctrl+v"));
        h.Down("lctrl");
        h.Down("rctrl");
        h.Up("lctrl"); // rctrl 还按着

        Assert.True(h.Down("v"));
        Assert.Single(h.Dispatched);
    }

    [Fact]
    public void 丢了一次抬起事件_下一次按下不会被误判成长按重复()
    {
        var h = new Harness(Bind("ctrl+alt+shift+f23"));
        h.Down("lctrl"); h.Down("lalt"); h.Down("lshift");
        Assert.True(h.Down("f23"));
        h.LoseUp("f23"); // 系统因钩子回调超时跳过了 f23 的抬起事件(GC 暂停时实测发生过)
        h.Up("lshift"); h.Up("lalt"); h.Up("lctrl");
        h.Wait(2000); // f23 被吞过,系统状态问不到;靠"间隔远超自动重复"判断这是新的按下

        h.Down("lctrl"); h.Down("lalt"); h.Down("lshift");
        Assert.True(h.Down("f23"));

        Assert.Equal(2, h.Dispatched.Count); // 第二次必须照常触发
        Assert.Equal(1, h.Pipeline.Resyncs);
    }

    [Fact]
    public void 修饰键的抬起丢了_比如Win加L锁屏_下一次按键重新同步_不会永久卡住()
    {
        var h = new Harness(Bind("v"), Bind("win+v", command: "test.win"));
        h.Down("lwin");
        h.LoseUp("lwin"); // 锁屏/UAC 安全桌面:Win 的抬起事件永远不会到达钩子

        Assert.True(h.Down("v")); // 若 Win 仍被记为按下,这里会匹配成 win+v

        Assert.Equal("test.run", Assert.Single(h.Dispatched).Command.ToString());
        Assert.Equal(1, h.Pipeline.Resyncs);
    }

    [Fact]
    public void 真实的长按重复_不会被误判为丢事件()
    {
        var h = new Harness(Bind("f2"));

        h.Down("f2"); // 物理上一直按着
        h.Down("f2");
        h.Down("f2");

        Assert.Single(h.Dispatched);
        Assert.Equal(0, h.Pipeline.Resyncs);
    }

    [Fact]
    public void 按进程作用域_前台进程不符时不触发也不吞()
    {
        var h = new Harness(Bind("f2", scope: new ProcessScope(["code"], Exclude: false)));
        h.Foreground = "notepad";

        Assert.False(h.Down("f2"));
        h.Up("f2");
        h.Foreground = "Code";

        Assert.True(h.Down("f2"));
        Assert.Single(h.Dispatched);
    }

    [Fact]
    public void 禁用的绑定_不生效()
    {
        var h = new Harness(Bind("f2", enabled: false));

        Assert.False(h.Down("f2"));
    }

    private sealed class DelegateFilter(Func<KeyEvent, FilterDecision> onKeyDown) : IRealtimeKeyFilter
    {
        public int Calls { get; private set; }

        public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter)
        {
            Calls++;
            return onKeyDown(keyEvent);
        }
    }

    [Fact]
    public void 过滤器吞键_后面的匹配不再进行()
    {
        var h = new Harness(Bind("f2"));
        h.Pipeline.AddFilter(new DelegateFilter(e => e.Key == "f2" ? FilterDecision.Consume : FilterDecision.Pass));

        Assert.True(h.Down("f2"));

        Assert.Empty(h.Dispatched);
    }

    [Fact]
    public void 过滤器抛异常_记录错误并放行_不拖垮管线()
    {
        var h = new Harness(Bind("f2"));
        h.Pipeline.AddFilter(new DelegateFilter(_ => throw new InvalidOperationException("过滤器 bug")));

        Assert.True(h.Down("f2")); // 仍然继续做快捷键匹配

        Assert.Single(h.Errors);
        Assert.Single(h.Dispatched);
    }

    private sealed class ResettableFilter : IRealtimeKeyFilter
    {
        public int Resets { get; private set; }

        public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter) =>
            keyEvent.Key == "capslock" ? FilterDecision.Consume : FilterDecision.Pass;

        public void OnReset(IKeyEmitter emitter)
        {
            Resets++;
            emitter.KeyUp("up"); // 释放自己发出的键
        }
    }

    [Fact]
    public void 锁屏解锁请求重置_下一个按键前通知过滤器_过滤器可以补发抬起()
    {
        var h = new Harness();
        var filter = new ResettableFilter();
        h.Pipeline.AddFilter(filter);
        Assert.True(h.Down("capslock")); // 被过滤器吞掉,之后锁屏,抬起收不到

        h.Pipeline.RequestReset();
        Assert.Equal(0, filter.Resets); // 不在调用线程上执行,等到钩子线程的下一个事件
        h.Down("a");

        Assert.Equal(1, filter.Resets);
        Assert.Equal([(0x26, false)], h.Sender.SentKeyEvents);
        Assert.True(h.Down("capslock")); // 重置后 capslock 是新的按下,照常交给过滤器
        Assert.Equal(1, filter.Resets);
    }

    [Fact]
    public void 过滤器token释放后_不再被调用()
    {
        var h = new Harness();
        var filter = new DelegateFilter(_ => FilterDecision.Pass);
        var token = h.Pipeline.AddFilter(filter);

        h.Down("a");
        token.Dispose();
        h.Down("b");

        Assert.Equal(1, filter.Calls);
    }

    [Fact]
    public async Task 键盘会话_收到事件_非修饰键被吞_修饰键不被吞()
    {
        var h = new Harness(Bind("tab")); // 会话期间不应触发快捷键
        await using var session = h.Pipeline.BeginSession(new KeyboardSessionOptions(ConsumeEvents: true));

        Assert.False(h.Down("lalt"));  // 修饰键不吞
        Assert.True(h.Down("tab"));    // 非修饰键吞
        Assert.True(h.Up("tab"));      // 对应的抬起也吞
        Assert.False(h.Up("lalt"));

        Assert.Empty(h.Dispatched);
        Assert.True(session.Events.TryRead(out var e1));
        Assert.Equal(("lalt", KeyEventKind.Down), (e1.Key, e1.Kind));
        Assert.True(session.Events.TryRead(out var e2));
        Assert.Equal(("tab", KeyEventKind.Down), (e2.Key, e2.Kind));
        Assert.Equal(KeyModifiers.Alt, e2.Modifiers);
    }

    [Fact]
    public async Task 会话在指定修饰键松开时自动结束_通道完成()
    {
        var h = new Harness();
        var session = h.Pipeline.BeginSession(new KeyboardSessionOptions(EndOnRelease: KeyModifiers.Alt));
        h.Down("lalt");
        h.Down("tab");

        h.Up("lalt");

        var events = new List<KeyEvent>();
        await foreach (var e in session.Events.ReadAllAsync().WithCancellation(new CancellationTokenSource(2000).Token))
        {
            events.Add(e); // 通道已完成,循环会自然结束
        }

        Assert.Equal(["lalt", "tab", "lalt"], events.Select(e => e.Key));
        Assert.Equal(KeyEventKind.Up, events[^1].Kind);
    }

    [Fact]
    public async Task 会话释放后_快捷键恢复正常()
    {
        var h = new Harness(Bind("f2"));
        var session = h.Pipeline.BeginSession(new KeyboardSessionOptions());
        Assert.True(h.Down("f2")); // 会话吞掉,没有分发
        h.Up("f2");
        Assert.Empty(h.Dispatched);

        await session.DisposeAsync();

        Assert.True(h.Down("f2"));
        Assert.Single(h.Dispatched);
    }

    [Fact]
    public void 会话开始前按下的键_在会话中抬起时不被吞_避免目标程序卡键()
    {
        var h = new Harness();
        h.Down("a"); // 会话开始前按下,已放行给目标程序
        var session = h.Pipeline.BeginSession(new KeyboardSessionOptions(ConsumeEvents: true));

        Assert.False(h.Up("a")); // 若吞了抬起,目标程序会以为 a 一直按着

        _ = session;
    }

    [Fact]
    public async Task 会话吞掉Win或Alt组合的主键时_插入屏蔽键_避免弹出开始菜单()
    {
        var h = new Harness();
        await using var session = h.Pipeline.BeginSession(new KeyboardSessionOptions(ConsumeEvents: true));

        h.Down("lwin");
        Assert.True(h.Down("v"));
        h.Up("v");
        h.Up("lwin");
        Assert.Equal(1, h.Sender.MaskTaps);

        h.Down("lctrl");
        h.Down("v");
        h.Up("v");
        h.Up("lctrl");
        Assert.Equal(1, h.Sender.MaskTaps); // Ctrl 组合不需要
    }

    [Fact]
    public async Task 录制_F24等高位F键_单独按下即可录到()
    {
        var h = new Harness();
        await using var session = h.Pipeline.BeginSession(new KeyboardSessionOptions(ConsumeEvents: true));

        Assert.True(h.Down("f24")); // 会话吞掉,不会传给其他程序
        Assert.True(session.Events.TryRead(out var e));

        var step = new ChordRecorder().Feed(e);
        Assert.Equal(ChordRecordOutcome.Recorded, step.Result?.Outcome);
        Assert.Equal(KeyChord.Parse("f24"), step.Result?.Chord);
    }
}

public class KeyNamesAndMatcherTests
{
    [Theory]
    [InlineData("a", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("f1", 0x70)]
    [InlineData("f24", 0x87)]
    [InlineData("space", 0x20)]
    [InlineData("lalt", 0xA4)]
    [InlineData("esc", 0x1B)]
    public void 键名与虚拟键码互转(string name, int vk)
    {
        Assert.True(KeyNames.TryGetVk(name, out var actualVk));
        Assert.Equal(vk, actualVk);
        Assert.True(KeyNames.TryGetName(vk, out var actualName));
        Assert.Equal(name, actualName);
    }

    [Theory]
    [InlineData("Escape", "esc")]
    [InlineData("RETURN", "enter")]
    [InlineData("Del", "delete")]
    [InlineData("pgup", "pageup")]
    [InlineData("v", "v")]
    public void 别名统一成规范名(string alias, string canonical)
    {
        Assert.Equal(canonical, KeyNames.Canonicalize(alias));
        Assert.True(KeyNames.IsKnown(alias));
    }

    [Fact]
    public void 未知键名_不被认可()
    {
        Assert.False(KeyNames.IsKnown("vv"));
        Assert.False(KeyNames.IsKnown("ctrl")); // 通用修饰键名不是"主键"
    }

    [Fact]
    public void 修饰键位与方向键扩展标志()
    {
        Assert.Equal(KeyNames.LCtrl, KeyNames.ModifierBit(0xA2));
        Assert.Equal(0, KeyNames.ModifierBit(0x41));
        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Win, KeyNames.ToModifiers(KeyNames.RCtrl | KeyNames.LWin));
        Assert.True(KeyNames.IsExtended(0x25));  // 左方向键
        Assert.False(KeyNames.IsExtended(0x41)); // 字母键
    }

    private static CommandBinding Bind(string chord, BindingScope? scope = null) =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), "test.run", CommandArguments.Empty, scope ?? BindingScope.Global);

    [Fact]
    public void 匹配器_别名键名落到同一个键()
    {
        var matcher = BindingMatcher.Build([Bind("ctrl+escape")]);

        Assert.NotNull(matcher.Match(KeyChord.Parse("ctrl+esc"), null));
    }

    [Theory]
    [InlineData("notepad", true)]
    [InlineData("NOTEPAD.exe", true)]   // 不区分大小写,容忍 .exe 后缀
    [InlineData("chrome", false)]
    [InlineData(null, false)]           // 进程未知时,"只在这些进程里"不生效
    public void 作用域_只在指定进程里(string? process, bool expected)
    {
        var matcher = BindingMatcher.Build([Bind("f2", new ProcessScope(["notepad"], Exclude: false))]);

        Assert.Equal(expected, matcher.Match(KeyChord.Parse("f2"), process) is not null);
    }

    [Theory]
    [InlineData("game", false)]
    [InlineData("chrome", true)]
    [InlineData(null, true)]            // 进程未知时,"除这些进程外"生效
    public void 作用域_除指定进程外(string? process, bool expected)
    {
        var matcher = BindingMatcher.Build([Bind("f2", new ProcessScope(["game"], Exclude: true))]);

        Assert.Equal(expected, matcher.Match(KeyChord.Parse("f2"), process) is not null);
    }

    [Fact]
    public void 同一快捷键多条绑定_取第一个作用域命中的()
    {
        var specific = Bind("f2", new ProcessScope(["code"], Exclude: false));
        var fallback = Bind("f2", BindingScope.Global);
        var matcher = BindingMatcher.Build([specific, fallback]);

        Assert.Equal(specific.Id, matcher.Match(KeyChord.Parse("f2"), "code")!.Id);
        Assert.Equal(fallback.Id, matcher.Match(KeyChord.Parse("f2"), "notepad")!.Id);
    }
}
