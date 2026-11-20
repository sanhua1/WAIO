using System.Text.Json;
using Waio.Core.Bindings;
using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

/// <summary>热键录制规则(ChordRecorder)与图形化编辑绑定的规则(BindingRules),ADR-0011。</summary>
public class BindingEditingTests
{
    private static KeyEvent Down(string key, KeyModifiers modifiers = KeyModifiers.None, bool repeat = false) =>
        new(KeyEventKind.Down, key, modifiers, repeat);

    // ---- 录制 ----

    [Theory]
    [InlineData("v", "Ctrl, Shift", "ctrl+shift+v")]
    [InlineData("v", "Win", "win+v")]
    [InlineData("f24", "None", "f24")]
    [InlineData("f13", "Ctrl", "ctrl+f13")]
    [InlineData("escape", "Ctrl", "ctrl+esc")]
    public void 录制_非修饰键首次按下时连同当前修饰键一起录下(string key, string modifiers, string expected)
    {
        var step = new ChordRecorder().Feed(Down(key, Enum.Parse<KeyModifiers>(modifiers)));

        Assert.Equal(ChordRecordOutcome.Recorded, step.Result?.Outcome);
        Assert.Equal(KeyChord.Parse(expected), step.Result?.Chord);
    }

    [Theory]
    [InlineData("lctrl")]
    [InlineData("rshift")]
    [InlineData("lwin")]
    public void 录制_只按修饰键_继续等待(string modifier)
    {
        Assert.Null(new ChordRecorder().Feed(Down(modifier, KeyModifiers.Ctrl)).Result);
    }

    [Fact]
    public void 录制_抬起和长按重复_不算()
    {
        Assert.Null(new ChordRecorder().Feed(new KeyEvent(KeyEventKind.Up, "v", KeyModifiers.Ctrl, false)).Result);
        Assert.Null(new ChordRecorder().Feed(Down("v", KeyModifiers.Ctrl, repeat: true)).Result);
    }

    [Fact]
    public void 录制_单按Esc取消_单按退格或Delete清除()
    {
        Assert.Equal(ChordRecordOutcome.Cancelled, new ChordRecorder().Feed(Down("esc")).Result?.Outcome);
        Assert.Equal(ChordRecordOutcome.Cleared, new ChordRecorder().Feed(Down("backspace")).Result?.Outcome);
        Assert.Equal(ChordRecordOutcome.Cleared, new ChordRecorder().Feed(Down("delete")).Result?.Outcome);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("5")]
    [InlineData("space")]
    [InlineData("comma")]
    public void 录制_单按会影响打字的键_不接受并提示(string key)
    {
        var step = new ChordRecorder().Feed(Down(key));

        Assert.Null(step.Result);
        Assert.Contains("修饰键", step.Hint ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void 手动选键列表_包含F13到F24_不含修饰键()
    {
        Assert.Contains("f13", KeyNames.PrimaryKeys);
        Assert.Contains("f24", KeyNames.PrimaryKeys);
        Assert.DoesNotContain("lctrl", KeyNames.PrimaryKeys);
        Assert.All(KeyNames.PrimaryKeys, k => Assert.True(KeyNames.IsKnown(k), k));
        Assert.Equal(KeyNames.PrimaryKeys.Count, KeyNames.PrimaryKeys.Distinct().Count());
    }

    // ---- 编辑 ----

    private static readonly CommandId Show = "clipboard.show";
    private static readonly CommandId OpenApp = "system.openApp";

    private static readonly IReadOnlyList<FeatureManifest> Manifests =
    [
        new("clipboard", "剪贴板", "", "", Capability.None, [new CommandDescriptor(Show, "显示剪贴板历史")], []),
        new("system", "系统操作", "", "", Capability.None,
            [new CommandDescriptor(OpenApp, "打开程序", [new ParameterSpec("target", "路径", ParameterKind.FilePath, Required: true)])], []),
    ];

    private static CommandBinding Binding(string chord, CommandId command, CommandArguments? args = null, BindingScope? scope = null) =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, args ?? CommandArguments.Empty, scope ?? BindingScope.Global);

    private static CommandArguments Args(string name, object value) =>
        new(new Dictionary<string, JsonElement> { [name] = JsonSerializer.SerializeToElement(value) });

    [Fact]
    public void Upsert_已有的原位替换_新的追加到末尾()
    {
        var a = Binding("ctrl+1", Show);
        var b = Binding("ctrl+2", Show);
        var changed = a with { Enabled = false };
        var c = Binding("ctrl+3", Show);

        var list = BindingRules.Upsert(BindingRules.Upsert([a, b], changed), c);

        Assert.Equal([a.Id, b.Id, c.Id], list.Select(x => x.Id));
        Assert.False(list[0].Enabled);
    }

    [Fact]
    public void Remove_按id删除()
    {
        var a = Binding("ctrl+1", Show);
        var b = Binding("ctrl+2", Show);

        Assert.Equal([b.Id], BindingRules.Remove([a, b], a.Id).Select(x => x.Id));
    }

    [Fact]
    public void 检查_正常的绑定_没有问题()
    {
        Assert.Empty(BindingRules.Check(Binding("ctrl+alt+f24", Show), [], Manifests));
    }

    [Fact]
    public void 检查_必填参数为空_是错误()
    {
        var issues = BindingRules.Check(Binding("ctrl+alt+n", OpenApp, Args("target", "  ")), [], Manifests);

        Assert.Contains(issues, i => i is { Level: BindingIssueLevel.Error } && i.Message.Contains("路径"));
    }

    [Fact]
    public void 检查_命令不存在_是错误()
    {
        var issues = BindingRules.Check(Binding("ctrl+alt+n", "ghost.run"), [], Manifests);

        Assert.Contains(issues, i => i.Level == BindingIssueLevel.Error);
    }

    [Fact]
    public void 检查_单个字母键_是错误()
    {
        var issues = BindingRules.Check(Binding("a", Show), [], Manifests);

        Assert.Contains(issues, i => i.Level == BindingIssueLevel.Error);
    }

    [Fact]
    public void 检查_覆盖系统快捷键_只是提示()
    {
        var issues = BindingRules.Check(Binding("win+v", Show), [], Manifests);

        var issue = Assert.Single(issues);
        Assert.Equal(BindingIssueLevel.Warning, issue.Level);
        Assert.Contains("剪贴板历史", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 检查_和其他启用的绑定同键_提示冲突_作用域不重叠或已停用则不提示()
    {
        var mine = Binding("ctrl+alt+n", Show);
        var same = Binding("ctrl+alt+n", OpenApp, Args("target", "notepad.exe"));
        var disabled = same with { Id = Guid.NewGuid(), Enabled = false };
        var onlyCode = mine with { Id = Guid.NewGuid(), Scope = new ProcessScope(["code"], Exclude: false) };
        var onlyNotepad = mine with { Scope = new ProcessScope(["notepad"], Exclude: false) };

        Assert.Contains(BindingRules.Check(mine, [same], Manifests), i => i.Message.Contains("冲突"));
        Assert.DoesNotContain(BindingRules.Check(mine, [disabled], Manifests), i => i.Message.Contains("冲突"));
        Assert.DoesNotContain(BindingRules.Check(onlyNotepad, [onlyCode], Manifests), i => i.Message.Contains("冲突"));
        Assert.DoesNotContain(BindingRules.Check(mine, [mine], Manifests), i => i.Message.Contains("冲突")); // 不和自己比
    }

    [Fact]
    public void 清理参数_去掉未声明的和空字符串()
    {
        var args = new CommandArguments(new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement("notepad.exe"),
            ["arguments"] = JsonSerializer.SerializeToElement(" "),
            ["url"] = JsonSerializer.SerializeToElement("https://example.com"),
        });
        var descriptor = Manifests[1].Commands[0];

        var cleaned = BindingRules.CleanArguments(args, descriptor);

        Assert.Equal(["target"], cleaned.Values.Keys);
    }
}
