using Waio.Core.Bindings;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Tests;

/// <summary>"WAIO 快捷键"页的分组(对应 MAIO 的 ShortcutGroupTests)。</summary>
public class BindingGroupTests
{
    private static readonly IReadOnlyList<FeatureManifest> Manifests =
    [
        new("clipboard", "剪贴板", "", "C", Capability.None, [new CommandDescriptor("clipboard.show", "显示")], []),
        new("translate", "翻译", "", "T", Capability.None, [new CommandDescriptor("translate.show", "翻译")], []),
        new("system", "系统动作", "", "S", Capability.None,
        [
            new CommandDescriptor("system.openApp", "打开程序或文件", [new ParameterSpec("target", "路径", ParameterKind.FilePath, Required: true)]),
            new CommandDescriptor("system.openUrl", "打开网页", [new ParameterSpec("url", "网址", ParameterKind.Url, Required: true)]),
        ], []),
    ];

    private static CommandBinding Make(string command, string chord = "alt+a") =>
        new(Guid.NewGuid(), new HotkeyTrigger(KeyChord.Parse(chord)), command, CommandArguments.Empty, BindingScope.Global);

    [Fact]
    public void 按功能目录顺序分组_然后是未知命令和带参数的命令()
    {
        var url = Make("system.openUrl");
        var t1 = Make("translate.show");
        var app = Make("system.openApp");
        var c1 = Make("clipboard.show");
        var gone = Make("gone.command");
        var t2 = Make("translate.show", "alt+b");

        var groups = BindingGroups.Make([url, t1, app, c1, gone, t2], Manifests);

        Assert.Equal(["剪贴板", "翻译", "未知命令", "打开程序或文件", "打开网页"], groups.Select(g => g.Title));
        Assert.Equal([t1, t2], groups[1].Bindings);
        Assert.Equal([true, true, false, false, false], groups.Select(g => g.IsFeature));
        Assert.Equal("S", groups[3].Glyph); // 带参数的命令组用所属功能的图标
    }

    [Fact]
    public void 没有绑定的组不出现()
    {
        var groups = BindingGroups.Make([Make("clipboard.show")], Manifests);

        Assert.Equal(["clipboard"], groups.Select(g => g.Id));
    }
}
