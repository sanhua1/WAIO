using Waio.Feature.KeyMapping;
using Waio.Feature.KeyMapping.Domain;
using Waio.Feature.KeyMapping.Realtime;
using Waio.Kit.Commands;
using Waio.Kit.Input;
using Waio.Testing;

namespace Waio.Feature.KeyMapping.Tests;

/// <summary>Super Caps(ADR-0013):CapsLock 换层。编译、过滤器状态机、功能接线。</summary>
public class SuperCapsTests
{
    private sealed class Emitter : IKeyEmitter
    {
        public List<string> Log { get; } = [];

        public void KeyDown(string key) => Log.Add("+" + key);

        public void KeyUp(string key) => Log.Add("-" + key);
    }

    private readonly Emitter _emitter = new();
    private readonly RemapFilter _filter = new();
    private static readonly FakeInputService Input = new();

    private static CompileResult Compile(KeyMappingSettings settings) => RuleCompiler.Compile(settings, Input.NormalizeKey);

    private void Load(KeyMappingSettings? settings = null) => _filter.Publish(Compile(settings ?? new KeyMappingSettings()).Rules);

    private FilterDecision Down(string key, bool repeat = false) =>
        _filter.OnKey(new KeyEvent(KeyEventKind.Down, key, KeyModifiers.None, repeat), _emitter);

    private FilterDecision Up(string key) =>
        _filter.OnKey(new KeyEvent(KeyEventKind.Up, key, KeyModifiers.None, false), _emitter);

    private static LayerKeySettings Map(string? from, string? to) => new() { From = from, To = to };

    // ---- 编译 ----

    [Fact]
    public void 默认设置_启用SuperCaps_WASD方向键_QE翻页_XC行首行尾()
    {
        var result = Compile(new KeyMappingSettings());

        Assert.Empty(result.Errors);
        var layer = Assert.IsType<Layer>(result.Rules.Layer);
        Assert.Equal("capslock", layer.Trigger);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["w"] = "up", ["a"] = "left", ["s"] = "down", ["d"] = "right",
                ["q"] = "pageup", ["e"] = "pagedown", ["x"] = "home", ["c"] = "end",
            },
            layer.Map);
    }

    [Fact]
    public void 关闭_没有层_CapsLock是普通键()
    {
        var result = Compile(new KeyMappingSettings { SuperCaps = new() { Enabled = false } });

        Assert.Null(result.Rules.Layer);
        Assert.True(result.Rules.IsEmpty);
    }

    [Fact]
    public void 非法映射_只丢弃该条并报告原因()
    {
        var result = Compile(new KeyMappingSettings
        {
            SuperCaps = new()
            {
                Mappings =
                [
                    Map("w", "up"), Map("capslock", "esc"), Map("lshift", "up"), Map("nonsense", "up"),
                    Map("j", "capslock"), Map("W", "down"), null!, Map(null, "up"),
                ],
            },
        });

        Assert.Equal(new Dictionary<string, string> { ["w"] = "up" }, result.Rules.Layer!.Map);
        Assert.Equal(6, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("已经映射过"));
    }

    [Fact]
    public void 启用时_针对CapsLock的改键规则被忽略并说明原因()
    {
        var result = Compile(new KeyMappingSettings { Rules = [new() { From = "caps", Tap = "esc", Hold = "lctrl" }] });

        Assert.Equal(0, result.Rules.Count);
        Assert.Contains(result.Errors, e => e.Contains("Super Caps"));
    }

    [Fact]
    public void 映射列表为null_层仍生效_CapsLock照样被吞()
    {
        var result = Compile(new KeyMappingSettings { SuperCaps = new() { Mappings = null! } });

        Assert.Empty(result.Rules.Layer!.Map);
    }

    // ---- 过滤器 ----

    [Fact]
    public void 按住Caps按W_发上箭头_长按重复_松开发抬起_Caps和W都被吞()
    {
        Load();

        Assert.Equal(FilterDecision.Consume, Down("capslock"));
        Assert.Equal(FilterDecision.Consume, Down("w"));
        Assert.Equal(FilterDecision.Consume, Down("w", repeat: true));
        Assert.Equal(FilterDecision.Consume, Up("w"));
        Assert.Equal(FilterDecision.Consume, Up("capslock"));

        Assert.Equal(["+up", "+up", "-up"], _emitter.Log);
    }

    [Fact]
    public void 单独轻点或长按Caps_全部吞掉_什么都不发_不会切换大写()
    {
        Load();

        Assert.Equal(FilterDecision.Consume, Down("capslock"));
        Assert.Equal(FilterDecision.Consume, Down("capslock", repeat: true));
        Assert.Equal(FilterDecision.Consume, Up("capslock"));

        Assert.Empty(_emitter.Log);
    }

    [Fact]
    public void 没按Caps时_W就是W()
    {
        Load();

        Assert.Equal(FilterDecision.Pass, Down("w"));
        Assert.Equal(FilterDecision.Pass, Up("w"));
        Assert.Empty(_emitter.Log);
    }

    [Fact]
    public void 先松开Caps再松开W_发的仍是上箭头的抬起_不留卡住的键()
    {
        Load();
        Down("capslock");
        Down("w");
        Up("capslock");

        Assert.Equal(FilterDecision.Consume, Down("w", repeat: true)); // 仍按 ↑ 处理,直到 W 自己抬起
        Assert.Equal(FilterDecision.Consume, Up("w"));
        Assert.Equal(FilterDecision.Pass, Down("w"));

        Assert.Equal(["+up", "+up", "-up"], _emitter.Log);
    }

    [Fact]
    public void 按住Caps时_没映射的键和修饰键照常放行()
    {
        Load();
        Down("capslock");

        Assert.Equal(FilterDecision.Pass, Down("lshift"));
        Assert.Equal(FilterDecision.Pass, Down("j"));
        Assert.Equal(FilterDecision.Consume, Down("d")); // Shift 物理按着,目标程序看到 Shift+→
        Assert.Equal(FilterDecision.Pass, Up("j"));
        Assert.Equal(FilterDecision.Pass, Up("lshift"));

        Assert.Equal(["+right"], _emitter.Log);
    }

    [Fact]
    public void 按Caps之前就按着的W_它的重复和抬起照常放行()
    {
        Load();
        Assert.Equal(FilterDecision.Pass, Down("w"));
        Down("capslock");

        Assert.Equal(FilterDecision.Pass, Down("w", repeat: true));
        Assert.Equal(FilterDecision.Pass, Up("w"));
        Assert.Empty(_emitter.Log);
    }

    [Fact]
    public void 锁屏解锁后重置_释放已发出的方向键_不再当作按住Caps()
    {
        Load();
        Down("capslock");
        Down("w"); // 之后锁屏,W 和 Caps 的抬起都收不到

        _filter.OnReset(_emitter);

        Assert.Equal(["+up", "-up"], _emitter.Log);
        Assert.Equal(FilterDecision.Pass, Down("w"));
    }

    [Fact]
    public void 改设置时_正在按着的方向键先抬起()
    {
        Load();
        Down("capslock");
        Down("w");

        Load(new KeyMappingSettings { SuperCaps = new() { Mappings = [Map("w", "pageup")] } });
        Down("w", repeat: true);

        Assert.Equal(["+up", "-up"], _emitter.Log); // 切换后 W 不在映射中(它是切换前按下的),重复照常放行
    }

    [Fact]
    public void 关掉再打开SuperCaps_不沿用之前的按住状态()
    {
        Load();
        Down("capslock");
        Load(new KeyMappingSettings { SuperCaps = new() { Enabled = false } });
        Up("j"); // 任意事件让过滤器切换规则
        Load();

        Assert.Equal(FilterDecision.Pass, Down("w"));
    }

    [Fact]
    public void 与双功能键一起用_等待中的双功能键在按Caps时判为按住()
    {
        Load(new KeyMappingSettings { Rules = [new() { From = "f", Tap = "esc", Hold = "lctrl" }] });

        Down("f");
        Down("capslock");
        Down("a");

        Assert.Equal(["+lctrl", "+left"], _emitter.Log); // = Ctrl+←
    }

    // ---- 功能接线 ----

    [Fact]
    public async Task 启用时_请求关闭大写锁定_暂停期间CapsLock照常_恢复时再关一次()
    {
        var ctx = new FakeFeatureContext("keymap");
        var feature = new KeyMappingFeature(ctx);
        await feature.ActivateAsync(default);

        Assert.Equal([("capslock", false)], ctx.FakeInput.LockStateRequests);
        Assert.Equal(FilterDecision.Consume, ctx.FakeInput.Feed(new KeyEvent(KeyEventKind.Down, "capslock", KeyModifiers.None, false)));

        await ctx.Bus.PerformAsync(KeyMappingFeature.Toggle, CommandArguments.Empty);
        Assert.Equal(FilterDecision.Pass, ctx.FakeInput.Feed(new KeyEvent(KeyEventKind.Down, "capslock", KeyModifiers.None, false)));

        await ctx.Bus.PerformAsync(KeyMappingFeature.Toggle, CommandArguments.Empty);
        Assert.Equal(2, ctx.FakeInput.LockStateRequests.Count);
    }

    [Fact]
    public async Task 关闭SuperCaps时_不动大写锁定()
    {
        var ctx = new FakeFeatureContext("keymap");
        await ctx.FakeSettings.SaveAsync(new KeyMappingSettings { SuperCaps = new() { Enabled = false } });
        await new KeyMappingFeature(ctx).ActivateAsync(default);

        Assert.Empty(ctx.FakeInput.LockStateRequests);
    }
}

/// <summary>设置页在每条规则的卡片上显示"为什么不生效",靠 <see cref="CompileResult.RuleErrors"/> 按序号对上。</summary>
public class RuleErrorsTests
{
    private static readonly FakeInputService Input = new();

    [Fact]
    public void 每条被忽略的规则_按序号给出原因_不带前缀_好的规则没有记录()
    {
        var result = RuleCompiler.Compile(
            new KeyMappingSettings
            {
                Rules =
                [
                    new() { From = "rctrl", To = "apps" },
                    new() { From = "capslock", To = "esc" },
                    new() { From = "rctrl", To = "esc" },
                    new() { From = "a", To = "a" },
                ],
            },
            Input.NormalizeKey);

        Assert.Equal([1, 2, 3], result.RuleErrors.Keys.Order());
        Assert.StartsWith("CapsLock 已用作 Super Caps", result.RuleErrors[1]);
        Assert.Contains("已有规则", result.RuleErrors[2]);
        Assert.DoesNotContain("被忽略", result.RuleErrors[3]);
        Assert.Equal(1, result.Rules.Count);
    }
}
