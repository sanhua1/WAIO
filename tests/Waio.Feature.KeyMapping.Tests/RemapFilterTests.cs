using Waio.Feature.KeyMapping;
using Waio.Feature.KeyMapping.Domain;
using Waio.Feature.KeyMapping.Realtime;
using Waio.Kit.Input;
using Waio.Testing;

namespace Waio.Feature.KeyMapping.Tests;

/// <summary>过滤器的状态机:改键、双功能键的轻点/按住判定、规则热切换。</summary>
public class RemapFilterTests
{
    private sealed class Emitter : IKeyEmitter
    {
        public List<string> Log { get; } = [];

        public void KeyDown(string key) => Log.Add("+" + key);

        public void KeyUp(string key) => Log.Add("-" + key);
    }

    private readonly Emitter _emitter = new();
    private readonly RemapFilter _filter = new();
    private readonly FakeInputService _input = new();

    private void Load(params KeyRuleSettings[] rules) =>
        _filter.Publish(RuleCompiler.Compile(new KeyMappingSettings { Rules = [.. rules], SuperCaps = new() { Enabled = false } }, _input.NormalizeKey).Rules);

    private FilterDecision Down(string key, bool repeat = false) =>
        _filter.OnKey(new KeyEvent(KeyEventKind.Down, key, KeyModifiers.None, repeat), _emitter);

    private FilterDecision Up(string key) =>
        _filter.OnKey(new KeyEvent(KeyEventKind.Up, key, KeyModifiers.None, false), _emitter);

    private static KeyRuleSettings CapsTapHold() => new() { From = "capslock", Tap = "esc", Hold = "lctrl" };

    [Fact]
    public void 没有规则_全部放行_什么都不发()
    {
        Assert.Equal(FilterDecision.Pass, Down("a"));
        Assert.Equal(FilterDecision.Pass, Up("a"));
        Assert.Empty(_emitter.Log);
    }

    [Fact]
    public void 简单改键_按下重复抬起都转发_并吞掉原键()
    {
        Load(new KeyRuleSettings { From = "a", To = "b" });

        Assert.Equal(FilterDecision.Consume, Down("a"));
        Assert.Equal(FilterDecision.Consume, Down("a", repeat: true));
        Assert.Equal(FilterDecision.Consume, Up("a"));

        Assert.Equal(["+b", "+b", "-b"], _emitter.Log);
    }

    [Fact]
    public void 没有规则的键_放行不受影响()
    {
        Load(new KeyRuleSettings { From = "a", To = "b" });

        Assert.Equal(FilterDecision.Pass, Down("c"));
        Assert.Equal(FilterDecision.Pass, Up("c"));
        Assert.Empty(_emitter.Log);
    }

    [Fact]
    public void 双功能键_单独轻点_发出tap()
    {
        Load(CapsTapHold());

        Assert.Equal(FilterDecision.Consume, Down("capslock"));
        Assert.Empty(_emitter.Log); // 还没决定,什么都不发
        Assert.Equal(FilterDecision.Consume, Up("capslock"));

        Assert.Equal(["+esc", "-esc"], _emitter.Log);
    }

    [Fact]
    public void 双功能键_按住时按了别的键_先发hold再放行那个键()
    {
        Load(CapsTapHold());

        Down("capslock");
        var decision = Down("c");   // 触发 hold

        Assert.Equal(FilterDecision.Pass, decision);   // c 本身放行(此时 Ctrl 已按下)
        Assert.Equal(["+lctrl"], _emitter.Log);

        Up("c");
        Up("capslock");

        Assert.Equal(["+lctrl", "-lctrl"], _emitter.Log); // 没有 esc
    }

    [Fact]
    public void 双功能键_hold之后长按重复_不重复发hold()
    {
        Load(CapsTapHold());
        Down("capslock");
        Down("c");

        Assert.Equal(FilterDecision.Consume, Down("capslock", repeat: true));

        Assert.Equal(["+lctrl"], _emitter.Log);
    }

    [Fact]
    public void 双功能键_按住期间多个别的键_hold只发一次()
    {
        Load(CapsTapHold());
        Down("capslock");

        Down("c");
        Down("v");

        Assert.Equal(["+lctrl"], _emitter.Log);
    }

    [Fact]
    public void 双功能键_轻点后再按别的键_不会误触发hold()
    {
        Load(CapsTapHold());
        Down("capslock");
        Up("capslock");
        _emitter.Log.Clear();

        Down("c");

        Assert.Empty(_emitter.Log);
    }

    [Fact]
    public void 双功能键_自己的长按重复不会把自己判成hold()
    {
        Load(CapsTapHold());
        Down("capslock");
        Down("capslock", repeat: true);
        Down("capslock", repeat: true);
        Up("capslock");

        Assert.Equal(["+esc", "-esc"], _emitter.Log); // 仍是轻点
    }

    [Fact]
    public void 两个双功能键_按住第一个时按第二个_两个都判为按住()
    {
        Load(CapsTapHold(), new KeyRuleSettings { From = "rctrl", Tap = "enter", Hold = "rshift" });
        Down("capslock");

        Down("rctrl");

        Assert.Equal(["+lctrl"], _emitter.Log); // 被按下的 rctrl 自己此时仍是 Pending
        Up("rctrl");                            // rctrl 没等到别的键 → 轻点
        Assert.Equal(["+lctrl", "+enter", "-enter"], _emitter.Log);
    }

    [Fact]
    public void 丢了抬起事件_下次按下先收尾旧的hold()
    {
        Load(CapsTapHold());
        Down("capslock");
        Down("c");                  // 进入 Holding
        _emitter.Log.Clear();

        Down("capslock");           // 没有 Up,直接又来一次非重复的按下(抬起事件丢了)

        Assert.Equal(["-lctrl"], _emitter.Log);
    }

    [Fact]
    public void 规则切换时_释放旧规则里卡住的hold键()
    {
        Load(CapsTapHold());
        Down("capslock");
        Down("c");                  // Holding,lctrl 按着
        _emitter.Log.Clear();

        Load(new KeyRuleSettings { From = "x", To = "y" });  // 用户改了配置
        Down("d");                  // 下一个按键事件上才会切换

        Assert.Equal(["-lctrl"], _emitter.Log);
    }

    [Fact]
    public void 发布空规则_等于关闭映射()
    {
        Load(new KeyRuleSettings { From = "a", To = "b" });
        _filter.Publish(RuleSet.Empty);

        Assert.Equal(FilterDecision.Pass, Down("a"));
        Assert.Empty(_emitter.Log);
    }
}
