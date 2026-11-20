using Waio.Feature.KeyMapping;
using Waio.Feature.KeyMapping.Domain;
using Waio.Testing;

namespace Waio.Feature.KeyMapping.Tests;

public class RuleCompilerTests
{
    private static readonly FakeInputService Input = new();

    private static CompileResult Compile(params KeyRuleSettings[] rules) =>
        RuleCompiler.Compile(new KeyMappingSettings { Rules = [.. rules], SuperCaps = new() { Enabled = false } }, Input.NormalizeKey);

    private static KeyRuleSettings Remap(string from, string to) => new() { From = from, To = to };

    private static KeyRuleSettings TapHold(string from, string tap, string hold) => new() { From = from, Tap = tap, Hold = hold };

    [Fact]
    public void 合法的改键和双功能键_都编译成功()
    {
        var result = Compile(Remap("a", "b"), TapHold("capslock", "esc", "lctrl"));

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Rules.Count);
        Assert.Single(result.Rules.TapHoldRules);
    }

    [Fact]
    public void 键名会被规范化_别名和大小写()
    {
        var result = Compile(TapHold("Caps", "Escape", "LCTRL"));

        Assert.True(result.Rules.TryGet("capslock", out var rule));
        Assert.Equal("esc", rule.Tap);
        Assert.Equal("lctrl", rule.Hold);
    }

    [Fact]
    public void 没有配置_得到空规则集()
    {
        var result = RuleCompiler.Compile(null, Input.NormalizeKey);

        Assert.True(result.Rules.IsEmpty);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("nonsense", "b")]
    [InlineData("a", "nonsense")]
    [InlineData(null, "b")]
    [InlineData("a", null)]
    [InlineData("", "b")]
    [InlineData("a", "   ")]
    public void 键名无法识别或缺失_只丢弃该条并报告(string? from, string? to)
    {
        var result = Compile(Remap("x", "y"), new KeyRuleSettings { From = from, To = to });

        Assert.Equal(1, result.Rules.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void 同一个键有两条规则_保留第一条()
    {
        var result = Compile(Remap("a", "b"), Remap("a", "c"));

        Assert.Equal(1, result.Rules.Count);
        Assert.True(result.Rules.TryGet("a", out var rule));
        Assert.Equal("b", rule.To);
        Assert.Contains("已有规则", Assert.Single(result.Errors));
    }

    [Fact]
    public void 改成自己_丢弃()
    {
        var result = Compile(Remap("a", "A"));

        Assert.True(result.Rules.IsEmpty);
        Assert.Contains("没有意义", Assert.Single(result.Errors));
    }

    [Fact]
    public void to与tap_hold混用_丢弃()
    {
        var result = Compile(new KeyRuleSettings { From = "a", To = "b", Tap = "c", Hold = "d" });

        Assert.True(result.Rules.IsEmpty);
        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("esc", null)]
    [InlineData(null, "lctrl")]
    public void 双功能键缺tap或hold_丢弃(string? tap, string? hold)
    {
        var result = Compile(new KeyRuleSettings { From = "capslock", Tap = tap, Hold = hold });

        Assert.True(result.Rules.IsEmpty);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void 规则数量超过上限_截断并报告()
    {
        var input = new FakeInputService(); // 独立实例:不污染其他测试共用的键名表
        input.KnownKeys.UnionWith(Enumerable.Range(0, 300).Select(i => "k" + i));
        var many = Enumerable.Range(0, RuleCompiler.MaxRules + 10)
            .Select(i => Remap("k" + i, "a"))
            .ToArray();

        var result = RuleCompiler.Compile(new KeyMappingSettings { Rules = [.. many] }, input.NormalizeKey);

        Assert.Equal(RuleCompiler.MaxRules, result.Rules.Count);
        Assert.Contains(result.Errors, e => e.Contains("上限"));
    }

    [Fact]
    public void settings里Rules为null_不崩溃()
    {
        var result = RuleCompiler.Compile(new KeyMappingSettings { Rules = null!, SuperCaps = null! }, Input.NormalizeKey);

        Assert.True(result.Rules.IsEmpty);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void rules里有null条目_丢弃该条()
    {
        var result = RuleCompiler.Compile(
            new KeyMappingSettings { Rules = [null!, Remap("a", "b")] }, Input.NormalizeKey);

        Assert.Equal(1, result.Rules.Count);
        Assert.Single(result.Errors);
    }
}
