using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Infrastructure;
using Waio.Kit.Input;

namespace Waio.Feature.Launcher.Tests;

/// <summary>应用目录、图标、设置、清单。</summary>
public class CatalogTests
{
    [Fact]
    public void 清单_默认快捷键是Ctrl空格()
    {
        var binding = Assert.Single(LauncherFeature.Manifest.DefaultBindings);

        Assert.Equal(KeyChord.Parse("ctrl+space"), ((HotkeyTrigger)binding.Trigger).Chord);
        Assert.Equal(LauncherFeature.Show, binding.Command);
    }

    [Theory]
    [InlineData("卸载 微信", null, true)]
    [InlineData("Uninstall Git", null, true)]
    [InlineData("Git Bash", @"C:\Program Files\Git\unins000.exe", true)]
    [InlineData("Git Bash", @"C:\Program Files\Git\git-bash.exe", false)]
    public void 卸载程序的识别(string name, string? target, bool expected) =>
        Assert.Equal(expected, AppCatalog.IsUninstaller(new AppEntry("{GUID}\\x.exe", name, target)));

    [Fact]
    public void 商店应用_包名去掉发布者前缀也能搜()
    {
        var calculator = new AppEntry("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "计算器", null);

        Assert.True(calculator.IsPackaged);
        Assert.Equal("WindowsCalculator", LauncherItem.PackageAppName(calculator));
        Assert.Equal("Microsoft Store 应用", LauncherItem.App(calculator).Subtitle);
        Assert.Equal(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", LauncherItem.App(calculator).Target);
    }

    [Fact]
    public void 应用id_不分大小写_每次扫描都一样() =>
        Assert.Equal(
            LauncherItem.App(new AppEntry("{GUID}\\Code.exe", "Code", null)).Id,
            LauncherItem.App(new AppEntry("{guid}\\code.EXE", "VS Code", null)).Id);

    [Fact]
    public void 图标_没有透明通道的改成不透明()
    {
        byte[] pixels = [10, 20, 30, 0, 40, 50, 60, 0];

        AppIcons.FixAlpha(pixels);

        Assert.Equal([10, 20, 30, 255, 40, 50, 60, 255], pixels);
    }

    [Fact]
    public void 图标_没预乘的按透明度预乘_已经预乘的不动()
    {
        byte[] straight = [200, 100, 0, 128, 0, 0, 0, 255];
        byte[] premultiplied = [100, 50, 0, 128, 0, 0, 0, 255];

        AppIcons.FixAlpha(straight);
        AppIcons.FixAlpha(premultiplied);

        Assert.Equal([100, 50, 0, 128, 0, 0, 0, 255], straight);
        Assert.Equal([100, 50, 0, 128, 0, 0, 0, 255], premultiplied);
    }

    [Fact]
    public void 引擎列表_逐行解析_写错的指出第几行()
    {
        var (engines, errors) = EngineListText.Parse(
            "知乎 | zh | https://www.zhihu.com/search?q={query}\r\n\r\n坏的一行\nX | zh | https://x.com/?q={query}\nY | y | file:///{query}");

        Assert.Equal("zh", Assert.Single(engines).Keyword);
        Assert.Equal(3, errors.Count);
        Assert.StartsWith("第 3 行", errors[0]);
        Assert.Contains("重复", errors[1]);
    }

    [Fact]
    public void 引擎列表_格式化再解析_原样回来()
    {
        var (engines, errors) = EngineListText.Parse(EngineListText.Format(WebSearchEngine.Defaults()));

        Assert.Empty(errors);
        Assert.Equal(WebSearchEngine.Defaults().Select(e => e.Template), engines.Select(e => e.Template));
    }

    /// <summary>
    /// 真的读一遍本机开始菜单的"所有应用"并取一个图标:只读,不弹窗口、不碰键盘,用来确认 COM 槽位没写错。
    /// </summary>
    [Fact]
    public async Task 本机所有应用_能列出来并取到图标()
    {
        using var worker = new StaWorker("test-shell");

        var apps = await worker.RunAsync(AppCatalog.Scan);

        Assert.True(apps.Count > 5, $"只找到 {apps.Count} 个应用");
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.DisplayName)));
        Assert.Contains(apps, a => a.TargetPath is not null); // 传统程序能拿到快捷方式指向的 exe
        var icon = await worker.RunAsync(() => AppIcons.Load(LauncherItem.AppsFolderPrefix + apps[0].ParsingName, 32));
        Assert.NotNull(icon);
        Assert.Equal(icon.Width * icon.Height * 4, icon.Bgra.Length);
    }
}
