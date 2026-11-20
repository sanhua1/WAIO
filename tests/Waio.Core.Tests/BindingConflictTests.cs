using Waio.Core.Bindings;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

public class BindingConflictTests
{
    private static CommandBinding Make(string chord, BindingScope scope, bool enabled = true, string command = "a.b") =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, CommandArguments.Empty, scope, enabled);

    private static ProcessScope Only(params string[] names) => new(names, Exclude: false);

    private static ProcessScope Except(params string[] names) => new(names, Exclude: true);

    [Fact]
    public void 同一快捷键_两个全局绑定_冲突()
    {
        var conflicts = BindingConflictDetector.Detect([Make("ctrl+alt+v", BindingScope.Global), Make("ctrl+alt+v", BindingScope.Global)]);

        Assert.Single(conflicts);
    }

    [Fact]
    public void 不同快捷键_不冲突()
    {
        Assert.Empty(BindingConflictDetector.Detect([Make("ctrl+alt+v", BindingScope.Global), Make("ctrl+alt+c", BindingScope.Global)]));
    }

    [Fact]
    public void 被禁用的绑定_不参与冲突()
    {
        Assert.Empty(BindingConflictDetector.Detect([Make("f1", BindingScope.Global), Make("f1", BindingScope.Global, enabled: false)]));
    }

    [Theory]
    [InlineData(true, true, "a,b", "b,c", true)]      // 只在 {a,b} 与只在 {b,c}:b 重叠
    [InlineData(true, true, "a", "b", false)]         // 只在 a 与只在 b:不重叠
    [InlineData(true, true, "A", "a", true)]          // 进程名不区分大小写
    [InlineData(true, false, "a", "a", false)]        // 只在 a 与除 a 外:不重叠
    [InlineData(true, false, "a", "b", true)]         // 只在 a 与除 b 外:a 同时满足
    [InlineData(true, false, "a,b", "a,b", false)]    // 只在 {a,b} 与除 {a,b} 外:不重叠
    [InlineData(false, false, "a", "b", true)]        // 除 a 外与除 b 外:总有第三个进程
    public void 进程作用域重叠判定(bool firstIsOnly, bool secondIsOnly, string first, string second, bool expected)
    {
        BindingScope Build(bool only, string names) =>
            new ProcessScope(names.Split(','), Exclude: !only);

        var overlap = BindingConflictDetector.ScopesOverlap(Build(firstIsOnly, first), Build(secondIsOnly, second));

        Assert.Equal(expected, overlap);
    }

    [Fact]
    public void 全局与任何进程作用域_都算重叠()
    {
        Assert.True(BindingConflictDetector.ScopesOverlap(BindingScope.Global, Only("a")));
        Assert.True(BindingConflictDetector.ScopesOverlap(Except("a"), BindingScope.Global));
    }

    [Fact]
    public void 只在记事本和只在浏览器里用同一个键_不冲突()
    {
        var conflicts = BindingConflictDetector.Detect([Make("f2", Only("notepad")), Make("f2", Only("chrome"))]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void 系统保留快捷键能被识别()
    {
        Assert.Equal("剪贴板历史", KnownSystemShortcuts.Describe(KeyChord.Parse("win+v")));
        Assert.Null(KnownSystemShortcuts.Describe(KeyChord.Parse("ctrl+alt+f12")));
    }
}

public class ShortcutDirectoryTests
{
    [Fact]
    public void 先到先得_冲突时报告占用方()
    {
        var directory = new ShortcutDirectory();
        var chord = KeyChord.Parse("ctrl+alt+v");

        Assert.True(directory.TryClaim(chord, "clipboard", out _));
        Assert.False(directory.TryClaim(chord, "launcher", out var owner));
        Assert.Equal("clipboard", owner);
    }

    [Fact]
    public void 同一个owner重复登记_视为成功()
    {
        var directory = new ShortcutDirectory();
        var chord = KeyChord.Parse("f1");
        directory.TryClaim(chord, "a", out _);

        Assert.True(directory.TryClaim(chord, "a", out _));
    }

    [Fact]
    public void 只有占用方才能释放()
    {
        var directory = new ShortcutDirectory();
        var chord = KeyChord.Parse("f1");
        directory.TryClaim(chord, "a", out _);

        directory.Release(chord, "b");
        Assert.True(directory.IsClaimed(chord));

        directory.Release(chord, "a");
        Assert.False(directory.IsClaimed(chord));
    }
}
