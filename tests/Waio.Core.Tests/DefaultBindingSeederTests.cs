using System.Text;
using Waio.Core.Bindings;
using Waio.Core.Clipboard;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

public class DefaultBindingSeederTests
{
    private static FeatureManifest Feature(string id, params (string Chord, string Verb)[] defaults) =>
        new(id, id, "", "", Capability.None,
            [.. defaults.Select(d => new CommandDescriptor($"{id}.{d.Verb}", d.Verb))],
            [.. defaults.Select(d => new DefaultBinding(new HotkeyTrigger(KeyChord.Parse(d.Chord)), $"{id}.{d.Verb}"))]);

    private static CommandBinding UserBinding(string chord, string command = "other.run", bool enabled = true) =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, CommandArguments.Empty, BindingScope.Global, enabled);

    private static SeedResult Seed(FeatureManifest[] features, string[] seededFeatures, string[] seededBindings, params CommandBinding[] existing) =>
        DefaultBindingSeeder.Seed(features, seededFeatures, seededBindings, existing);

    [Fact]
    public void 首次启用_追加默认绑定_并记下功能和每一条绑定()
    {
        var result = Seed([Feature("clipboard", ("win+v", "show"))], [], []);

        var binding = Assert.Single(result.NewBindings);
        Assert.Equal("clipboard.show", binding.Command.Value);
        Assert.Equal(KeyChord.Parse("win+v"), ((HotkeyTrigger)binding.Trigger).Chord);
        Assert.True(binding.Enabled);
        Assert.Equal(["clipboard"], result.SeededFeatures);
        Assert.Equal(["clipboard.show|win+v"], result.SeededBindings);
    }

    [Fact]
    public void 处理过的默认绑定_不再处理_用户删掉的不会回来()
    {
        var result = Seed([Feature("clipboard", ("win+v", "show"))], ["clipboard"], ["clipboard.show|win+v"]);

        Assert.Empty(result.NewBindings);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void 功能后来新增的默认绑定_老配置也会加上()
    {
        // 截图:最初只有 Alt+F1,后来加了 Alt+F2、Alt+F3(ADR-0022 要修的问题)
        var screenshot = Feature("screenshot", ("alt+f1", "capture"), ("alt+f2", "pinClipboard"), ("alt+f3", "record"));
        var result = Seed([screenshot], ["screenshot"], ["screenshot.capture|alt+f1"], UserBinding("alt+f1", "screenshot.capture"));

        Assert.Equal(["screenshot.pinClipboard", "screenshot.record"], result.NewBindings.Select(b => b.Command.Value));
        Assert.Empty(result.SeededFeatures);
        Assert.Equal(["screenshot.pinClipboard|alt+f2", "screenshot.record|alt+f3"], result.SeededBindings);
    }

    [Fact]
    public void 新增的默认绑定_用户已经给这个命令绑了别的键_就不加但记下()
    {
        var result = Seed([Feature("screenshot", ("alt+f3", "record"))], ["screenshot"], [], UserBinding("ctrl+f9", "screenshot.record"));

        Assert.Empty(result.NewBindings);
        Assert.Equal(["screenshot.record|alt+f3"], result.SeededBindings);
    }

    [Fact]
    public void 新增的默认绑定_用户禁用了这个命令的绑定_也算用户的选择()
    {
        var result = Seed([Feature("screenshot", ("alt+f3", "record"))], ["screenshot"], [],
            UserBinding("alt+f3", "screenshot.record", enabled: false));

        Assert.Empty(result.NewBindings);
    }

    [Fact]
    public void 旧版本配置迁移_已播种功能里命令已有绑定的不重复加_没有绑定的补上()
    {
        // 旧 app.json:只有 seededFeatures,没有 seededBindings
        var result = Seed([Feature("clipboard", ("win+v", "show")), Feature("screenshot", ("alt+f1", "capture"), ("alt+f2", "pinClipboard"))],
            ["clipboard", "screenshot"], [],
            UserBinding("win+v", "clipboard.show"), UserBinding("alt+f1", "screenshot.capture"));

        Assert.Equal(["screenshot.pinClipboard"], result.NewBindings.Select(b => b.Command.Value));
        Assert.Equal(3, result.SeededBindings.Count); // 三条都记下,以后不再处理
    }

    [Fact]
    public void 默认键位改了_算一条新的默认绑定()
    {
        Assert.Equal("a.x|ctrl+f1", DefaultBindingSeeder.KeyOf(new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("ctrl+f1")), "a.x")));
        Assert.NotEqual(
            DefaultBindingSeeder.KeyOf(new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("ctrl+f1")), "a.x")),
            DefaultBindingSeeder.KeyOf(new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("ctrl+f2")), "a.x")));
    }

    [Fact]
    public void 键位已被用户占用_跳过该默认绑定_但仍记下()
    {
        var result = Seed([Feature("clipboard", ("win+v", "show"))], [], [], UserBinding("win+v", "my.thing"));

        Assert.Empty(result.NewBindings);
        Assert.Equal(["clipboard"], result.SeededFeatures);
        Assert.Equal(["clipboard.show|win+v"], result.SeededBindings); // 否则每次启动都重试并报同一条
        Assert.Contains("my.thing", Assert.Single(result.Skipped));
    }

    [Fact]
    public void 被禁用的用户绑定_不算占用键位()
    {
        var result = Seed([Feature("clipboard", ("win+v", "show"))], [], [], UserBinding("win+v", enabled: false));

        Assert.Single(result.NewBindings);
    }

    [Fact]
    public void 两个功能默认键位相同_先到先得()
    {
        var result = Seed([Feature("a", ("ctrl+f1", "x")), Feature("b", ("ctrl+f1", "y"))], [], []);

        Assert.Equal(["a.x"], result.NewBindings.Select(b => b.Command.Value));
        Assert.Equal(["a", "b"], result.SeededFeatures);
    }

    [Fact]
    public void 多个默认绑定_各自独立判断()
    {
        var result = Seed([Feature("f", ("ctrl+f1", "one"), ("ctrl+f2", "two"))], [], [], UserBinding("ctrl+f1"));

        Assert.Equal(["f.two"], result.NewBindings.Select(b => b.Command.Value));
    }

    [Fact]
    public void 没有默认绑定的功能_也记为已播种()
    {
        var result = Seed([Feature("plain")], [], []);

        Assert.Empty(result.NewBindings);
        Assert.Equal(["plain"], result.SeededFeatures);
        Assert.False(result.IsEmpty);
    }
}

public class ClipboardFormatsTests
{
    [Fact]
    public void DROPFILES_头部20字节_pFiles指向路径列表_宽字符标志置位()
    {
        var bytes = ClipboardFormatsIO.BuildDropFiles([@"C:\a.txt", @"D:\b c\d.png"]);

        Assert.Equal(20, BitConverter.ToInt32(bytes, 0));   // pFiles
        Assert.Equal(1, BitConverter.ToInt32(bytes, 16));   // fWide
        var paths = Encoding.Unicode.GetString(bytes, 20, bytes.Length - 20);
        Assert.Equal("C:\\a.txt\0D:\\b c\\d.png\0\0", paths); // 路径间单 NUL,列表以双 NUL 结尾
    }
}
