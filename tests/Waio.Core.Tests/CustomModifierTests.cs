using Waio.Core.Bindings;
using Waio.Core.Input;
using Waio.Core.Storage;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

/// <summary>自定义修饰键(ADR-0012):管线行为、录制、编辑检查、配置过滤。</summary>
public class CustomModifierTests
{
    private sealed class FakeSender : IKeySender
    {
        public List<(int Vk, bool Down)> Keys { get; } = [];

        public int MaskTaps { get; private set; }

        public void Send(KeyChord chord)
        {
        }

        public void SendKey(int vk, bool down) => Keys.Add((vk, down));

        public void TapMaskKey() => MaskTaps++;
    }

    private sealed class FakeKeyboard : IKeyStateProbe
    {
        public HashSet<int> Physical { get; } = [];

        public bool IsDown(int vk) => Physical.Contains(vk);
    }

    private sealed class Harness
    {
        public Harness(params string[] chords)
        {
            Pipeline = new InputPipeline(Sender, Keyboard, Dispatched.Add, () => "notepad", _ => { });
            Pipeline.SetMatcher(BindingMatcher.Build(chords.Select(c =>
                new CommandBinding(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(c)), "test.run", CommandArguments.Empty, BindingScope.Global))));
            Pipeline.SetCustomModifiers(["f24"]);
        }

        public FakeSender Sender { get; } = new();

        public FakeKeyboard Keyboard { get; } = new();

        public List<CommandBinding> Dispatched { get; } = [];

        public InputPipeline Pipeline { get; }

        public IEnumerable<string> Fired => Dispatched.Select(b => ((HotkeyTrigger)b.Trigger).Chord.ToString());

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

        public static int Vk(string key) => KeyNames.TryGetVk(key, out var vk) ? vk : throw new ArgumentException(key);
    }

    // ---- 管线 ----

    [Fact]
    public void 按住F24再按J_触发f24加j_F24和J都被吞()
    {
        var h = new Harness("f24+j");

        Assert.True(h.Down("f24"));
        Assert.True(h.Down("j"));
        Assert.True(h.Up("j"));
        Assert.True(h.Up("f24"));

        Assert.Equal(["f24+j"], h.Fired);
        Assert.Empty(h.Sender.Keys); // 用过了,松开时不补发 F24
    }

    [Fact]
    public void 单独轻点F24_没有对应快捷键_把F24补发给目标程序()
    {
        var h = new Harness("f24+j");

        Assert.True(h.Down("f24"));
        Assert.True(h.Up("f24"));

        Assert.Empty(h.Dispatched);
        Assert.Equal([(Harness.Vk("f24"), true), (Harness.Vk("f24"), false)], h.Sender.Keys);
    }

    [Fact]
    public void 单独轻点F24_有单独的f24快捷键_松开时触发_不补发()
    {
        var h = new Harness("f24", "f24+j");

        Assert.True(h.Down("f24"));
        Assert.Empty(h.Dispatched); // 按下时还不知道是轻点还是组合
        h.Up("f24");

        Assert.Equal(["f24"], h.Fired);
        Assert.Empty(h.Sender.Keys);
    }

    [Fact]
    public void 长按F24的自动重复也被吞_不会补发多次()
    {
        var h = new Harness();

        h.Down("f24");
        Assert.True(h.Down("f24")); // 重复
        Assert.True(h.Down("f24"));
        h.Up("f24");

        Assert.Equal(2, h.Sender.Keys.Count); // 只补发一次按下+抬起
    }

    [Fact]
    public void 按住F24时按了没绑定的键_该键照常放行_F24松开时不补发()
    {
        var h = new Harness("f24+j");

        h.Down("f24");
        Assert.False(h.Down("k"));
        Assert.False(h.Up("k"));
        h.Up("f24");

        Assert.Empty(h.Dispatched);
        Assert.Empty(h.Sender.Keys);
    }

    [Fact]
    public void 标准修饰键加F24组合()
    {
        var h = new Harness("ctrl+f24+j");

        h.Down("lctrl");
        h.Down("f24");
        Assert.True(h.Down("j"));

        Assert.Equal(["ctrl+f24+j"], h.Fired);
    }

    [Fact]
    public void 没按F24时_J就是普通的J()
    {
        var h = new Harness("f24+j", "ctrl+j");

        Assert.False(h.Down("j"));
        h.Up("j");
        h.Down("lctrl");
        Assert.True(h.Down("j"));

        Assert.Equal(["ctrl+j"], h.Fired);
    }

    [Fact]
    public void 回归_按住F24期间按Z_系统报告F24没按着也不影响组合()
    {
        // 真实系统里被吞掉的 F24 按下不会更新 GetAsyncKeyState;早先用它核对,把 F24 的按住状态清掉了
        var h = new Harness("f24+z");

        h.Down("f24");
        h.Down("f24"); // 长按的自动重复
        h.Down("f24");
        Assert.DoesNotContain(Harness.Vk("f24"), h.Keyboard.Physical);
        Assert.True(h.Down("z"));
        h.Up("z");
        h.Up("f24");

        Assert.Equal(["f24+z"], h.Fired);
        Assert.Empty(h.Sender.Keys);
    }

    [Fact]
    public void F24的抬起丢了_锁屏解锁后重置_不会一直带着F24()
    {
        var h = new Harness("f24+j", "ctrl+j");

        h.Down("f24"); // 之后锁屏,抬起永远收不到
        h.Pipeline.RequestReset();
        h.Down("lctrl");
        h.Down("j");

        Assert.Equal(["ctrl+j"], h.Fired);
    }

    [Fact]
    public void F24的抬起丢了_隔一段时间再按F24_当作新的按下_之后恢复正常()
    {
        var h = new Harness("f24", "f24+j");

        h.Down("f24"); // 抬起丢了
        h.Wait(3000);
        h.Down("f24"); // 新的按下(不是自动重复)
        h.Up("f24");

        Assert.Equal(["f24"], h.Fired);
        h.Down("j");
        Assert.Equal(["f24"], h.Fired); // F24 已松开,J 不再和它组合
    }

    [Fact]
    public void 未声明为自定义修饰键时_F24是普通键_按下立即触发()
    {
        var h = new Harness("f24");
        h.Pipeline.SetCustomModifiers([]);

        Assert.True(h.Down("f24"));

        Assert.Equal(["f24"], h.Fired);
    }

    // ---- 录制 ----

    private static KeyEvent Ev(KeyEventKind kind, string key, KeyModifiers modifiers = KeyModifiers.None) => new(kind, key, modifiers, false);

    [Fact]
    public void 录制_按住F24再按J_录成f24加j()
    {
        var recorder = new ChordRecorder(new HashSet<string> { "f24" });

        Assert.Null(recorder.Feed(Ev(KeyEventKind.Down, "f24")).Result);
        var step = recorder.Feed(Ev(KeyEventKind.Down, "j", KeyModifiers.Ctrl));

        Assert.Equal(KeyChord.Parse("ctrl+f24+j"), step.Result?.Chord);
    }

    [Fact]
    public void 录制_F24按下又松开_录成单独的f24()
    {
        var recorder = new ChordRecorder(new HashSet<string> { "f24" });

        recorder.Feed(Ev(KeyEventKind.Down, "f24"));
        var step = recorder.Feed(Ev(KeyEventKind.Up, "f24"));

        Assert.Equal(KeyChord.Parse("f24"), step.Result?.Chord);
    }

    [Fact]
    public void 录制_按住F24再按字母_不受单键限制()
    {
        var recorder = new ChordRecorder(new HashSet<string> { "f24" });

        recorder.Feed(Ev(KeyEventKind.Down, "f24"));
        var step = recorder.Feed(Ev(KeyEventKind.Down, "a"));

        Assert.Equal(ChordRecordOutcome.Recorded, step.Result?.Outcome);
        Assert.Equal("f24", step.Result?.Chord.Hold);
    }

    // ---- 检查与配置 ----

    private static readonly IReadOnlyList<FeatureManifest> Manifests =
        [new("t", "测试", "", "", Capability.None, [new CommandDescriptor("test.run", "运行")], [])];

    private static CommandBinding Binding(string chord) =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), "test.run", CommandArguments.Empty, BindingScope.Global);

    [Fact]
    public void 检查_用了未声明的自定义修饰键_是错误_声明后通过()
    {
        Assert.Contains(BindingRules.Check(Binding("f24+j"), [], Manifests, []), i => i.Level == BindingIssueLevel.Error);
        Assert.Empty(BindingRules.Check(Binding("f24+j"), [], Manifests, ["f24"]));
    }

    [Fact]
    public void 检查_字母不能做自定义修饰键()
    {
        Assert.Contains(BindingRules.Check(Binding("a+j"), [], Manifests, ["a"]), i => i.Level == BindingIssueLevel.Error);
    }

    [Fact]
    public void 绑定文件_接受f24加j_拒绝字母当自定义修饰键()
    {
        var ok = new BindingDto { Id = Guid.NewGuid(), Trigger = new TriggerDto { Hotkey = "Ctrl+F24+J" }, Command = "test.run" };
        var bad = new BindingDto { Id = Guid.NewGuid(), Trigger = new TriggerDto { Hotkey = "a+j" }, Command = "test.run" };

        Assert.True(BindingMapper.TryFromDto(ok, out var binding, out _));
        Assert.Equal(KeyChord.Parse("ctrl+f24+j"), ((HotkeyTrigger)binding!.Trigger).Chord);
        Assert.False(BindingMapper.TryFromDto(bad, out _, out var error));
        Assert.Contains("自定义修饰键", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 应用设置_只保留合法的自定义修饰键_规范化去重()
    {
        var settings = new AppSettings { CustomModifiers = ["F24", "f24", "a", "caps", "nonsense"] };

        Assert.Equal(["f24", "capslock"], settings.ValidCustomModifiers());
    }
}
