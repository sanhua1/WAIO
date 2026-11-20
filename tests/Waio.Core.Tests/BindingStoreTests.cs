using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Bindings;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

public class BindingStoreTests
{
    private static BindingStore Store(TempRoot root) => new(root.Paths, NullLogger.Instance);

    private static void WriteFile(TempRoot root, string json)
    {
        Directory.CreateDirectory(root.Paths.ConfigDirectory);
        File.WriteAllText(root.Paths.BindingsFile, json);
    }

    private static CommandBinding Binding(string chord, string command, BindingScope? scope = null, bool enabled = true) =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, CommandArguments.Empty, scope ?? BindingScope.Global, enabled);

    [Fact]
    public void 文件不存在_返回空且无错误()
    {
        using var root = new TempRoot();

        var result = Store(root).Load();

        Assert.Empty(result.Bindings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task 保存后读回_内容一致()
    {
        using var root = new TempRoot();
        var args = new CommandArguments(new Dictionary<string, JsonElement>
        {
            ["path"] = JsonDocument.Parse("\"notepad.exe\"").RootElement.Clone(),
        });
        var original = new[]
        {
            new CommandBinding(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse("ctrl+alt+n")), "system.openApp", args, BindingScope.Global),
            Binding("win+shift+x", "clipboard.showPanel", new ProcessScope(["game", "Vm"], Exclude: true), enabled: false),
        };

        await Store(root).SaveAsync(original);
        var loaded = Store(root).Load();

        Assert.Empty(loaded.Errors);
        Assert.Equal(2, loaded.Bindings.Count);
        Assert.Equal(original[0].Id, loaded.Bindings[0].Id);
        Assert.Equal("notepad.exe", loaded.Bindings[0].Arguments.GetString("path"));
        var scope = Assert.IsType<ProcessScope>(loaded.Bindings[1].Scope);
        Assert.True(scope.Exclude);
        Assert.Equal(["game", "Vm"], scope.ProcessNames);
        Assert.False(loaded.Bindings[1].Enabled);
    }

    [Fact]
    public void 单条非法_只丢弃该条并报告_其余照常加载()
    {
        using var root = new TempRoot();
        var good = Guid.NewGuid();
        WriteFile(root, $$"""
        { "schemaVersion": 1, "bindings": [
          { "id": "{{good}}", "trigger": { "hotkey": "ctrl+alt+v" }, "command": "clipboard.showPanel" },
          { "id": "{{Guid.NewGuid()}}", "trigger": { "hotkey": "ctrl+alt+x" }, "command": "不是合法命令" },
          { "id": "{{Guid.NewGuid()}}", "trigger": { "hotkey": "ctrl+" }, "command": "a.b" },
          { "id": "{{Guid.NewGuid()}}", "trigger": { "gesture": "swipe" }, "command": "a.b" },
          { "trigger": { "hotkey": "f1" }, "command": "a.b" },
          { "id": "{{Guid.NewGuid()}}", "trigger": { "hotkey": "f2" }, "command": "a.b", "scope": { "type": "mystery" } },
          { "id": "{{Guid.NewGuid()}}", "trigger": { "hotkey": "f3" }, "command": "a.b", "scope": { "type": "process" } }
        ] }
        """);

        var result = Store(root).Load();

        Assert.Equal(good, Assert.Single(result.Bindings).Id);
        Assert.Equal(6, result.Errors.Count);
    }

    [Fact]
    public void 有条目被丢弃时_保留原文件副本以免数据丢失()
    {
        using var root = new TempRoot();
        WriteFile(root, """{ "bindings": [ { "id": "11111111-1111-1111-1111-111111111111", "trigger": { "hotkey": "ctrl+" }, "command": "a.b" } ] }""");

        Store(root).Load();

        Assert.True(File.Exists(root.Paths.BindingsFile)); // 原文件继续在位
        Assert.Single(Directory.GetFiles(root.Paths.ConfigDirectory, "bindings.json.lossy-*"));
    }

    [Fact]
    public void id重复_后一条被忽略()
    {
        using var root = new TempRoot();
        var id = Guid.NewGuid();
        WriteFile(root, $$"""
        { "bindings": [
          { "id": "{{id}}", "trigger": { "hotkey": "f1" }, "command": "a.one" },
          { "id": "{{id}}", "trigger": { "hotkey": "f2" }, "command": "a.two" }
        ] }
        """);

        var result = Store(root).Load();

        Assert.Equal("a.one", Assert.Single(result.Bindings).Command.ToString());
        Assert.Contains("重复", Assert.Single(result.Errors));
    }

    [Fact]
    public void 整个文件不是JSON_备份并以空列表启动()
    {
        using var root = new TempRoot();
        WriteFile(root, "<<<垃圾>>>");

        var result = Store(root).Load();

        Assert.Empty(result.Bindings);
        Assert.Single(result.Errors);
        Assert.False(File.Exists(root.Paths.BindingsFile));
        Assert.Single(Directory.GetFiles(root.Paths.ConfigDirectory, "bindings.json.bad-*"));
    }

    [Fact]
    public void 缺省作用域视为全局_缺省enabled视为启用()
    {
        using var root = new TempRoot();
        WriteFile(root, $$"""{ "bindings": [ { "id": "{{Guid.NewGuid()}}", "trigger": { "hotkey": "f5" }, "command": "a.b" } ] }""");

        var binding = Assert.Single(Store(root).Load().Bindings);

        Assert.IsType<GlobalScope>(binding.Scope);
        Assert.True(binding.Enabled);
    }
}
