using Waio.Core.Bindings;
using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

/// <summary>系统快捷键清单与占用状态(ADR-0029)。</summary>
public class SystemShortcutTests
{
    private static readonly CommandId Show = "demo.show";

    private static readonly IReadOnlyList<FeatureManifest> Manifests =
    [
        new("demo", "演示", "", "", Capability.None, [new CommandDescriptor(Show, "显示")], []),
    ];

    private static CommandBinding Make(string chord, BindingScope? scope = null, bool enabled = true, string command = "demo.show") =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, CommandArguments.Empty, scope ?? BindingScope.Global, enabled);

    private static bool Available(CommandId id) => id == Show;

    private static SystemShortcutUsage UsageOf(IEnumerable<CommandBinding> bindings, string chord) =>
        SystemShortcutUsages.Evaluate(bindings, Available).Single(u => u.Shortcut.Chord == KeyChord.Parse(chord));

    [Fact]
    public void 清单_没有重复且每条都是规范写法的已知按键()
    {
        var all = KnownSystemShortcuts.All;

        Assert.True(all.Count > 100);
        Assert.Equal(all.Count, all.Select(s => s.Chord).Distinct().Count());
        Assert.All(all, s =>
        {
            Assert.True(KeyNames.IsKnown(s.Chord.Key), s.Chord.ToString());
            Assert.Equal(KeyNames.Canonicalize(s.Chord), s.Chord);
            Assert.Null(s.Chord.Hold);
            Assert.False(string.IsNullOrWhiteSpace(s.Function));
        });
    }

    [Theory]
    [InlineData("win+r", "运行")]
    [InlineData("WIN+Escape", "关闭放大镜")]
    [InlineData("win+shift+3", "新开一个任务栏第 3 个程序")]
    [InlineData("win+0", "打开 / 切换到任务栏第 10 个程序")]
    public void 查找_组合会先规范化(string chord, string function)
    {
        Assert.Equal(function, KnownSystemShortcuts.Describe(KeyChord.Parse(chord)));
    }

    [Fact]
    public void 查找_带自定义修饰键的组合_不是系统快捷键()
    {
        Assert.Null(KnownSystemShortcuts.Find(new KeyChord(KeyModifiers.Win, "r", "f24")));
    }

    [Theory]
    [InlineData("win+l")]
    [InlineData("ctrl+alt+delete")]
    public void 系统保留键_不能接管(string chord)
    {
        Assert.False(KnownSystemShortcuts.Find(KeyChord.Parse(chord))!.CanOverride);
    }

    [Fact]
    public void 检查_绑定系统保留键_是错误()
    {
        var issues = BindingRules.Check(Make("win+l"), [], Manifests);

        Assert.Contains(issues, i => i.Level == BindingIssueLevel.Error && i.Message.Contains("锁定电脑"));
    }

    [Fact]
    public void 状态_没有绑定_系统照常()
    {
        Assert.Equal(SystemShortcutState.System, UsageOf([], "win+r").State);
    }

    [Fact]
    public void 状态_全局绑定_被接管()
    {
        var binding = Make("win+r");

        var usage = UsageOf([binding], "win+r");

        Assert.Equal(SystemShortcutState.TakenOver, usage.State);
        Assert.Equal(binding, Assert.Single(usage.Bindings));
    }

    [Fact]
    public void 状态_停用的或命令不可用的绑定_不算占用()
    {
        Assert.Equal(SystemShortcutState.System, UsageOf([Make("win+r", enabled: false)], "win+r").State);
        Assert.Equal(SystemShortcutState.System, UsageOf([Make("win+r", command: "gone.cmd")], "win+r").State);
    }

    [Fact]
    public void 状态_只在部分程序生效_部分接管_有全局的就是完全接管()
    {
        var scoped = Make("win+e", new ProcessScope(["code"], Exclude: false));

        Assert.Equal(SystemShortcutState.PartlyTakenOver, UsageOf([scoped], "win+e").State);
        Assert.Equal(SystemShortcutState.TakenOver, UsageOf([scoped, Make("win+e")], "win+e").State);
    }

    [Fact]
    public void 状态_绑了系统保留键_两边都会触发()
    {
        Assert.Equal(SystemShortcutState.Clash, UsageOf([Make("win+l")], "win+l").State);
    }

    [Fact]
    public void 单条绑定_返回它占用的系统快捷键()
    {
        Assert.Equal("win+v", SystemShortcutUsages.For(Make("win+v"), Available)?.Shortcut.Chord.ToString());
        Assert.Null(SystemShortcutUsages.For(Make("ctrl+alt+f12"), Available));
        Assert.Null(SystemShortcutUsages.For(Make("win+v", enabled: false), Available));
    }

    [Fact]
    public void 恢复系统功能_停用所有占用的绑定_其他不动()
    {
        var a = Make("win+r");
        var b = Make("WIN+R", command: "gone.cmd");
        var other = Make("win+e");

        var result = SystemShortcutUsages.Release([a, b, other], KeyChord.Parse("win+r"));

        Assert.False(result.Single(x => x.Id == a.Id).Enabled);
        Assert.False(result.Single(x => x.Id == b.Id).Enabled);
        Assert.True(result.Single(x => x.Id == other.Id).Enabled);
        Assert.Equal([a.Id, b.Id, other.Id], result.Select(x => x.Id));
    }
}
