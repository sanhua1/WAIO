using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Launch;
using Waio.Core.Storage;
using Waio.Kit.Features;

namespace Waio.Core.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static FeatureManifest Manifest(string id, bool enabledByDefault = true) =>
        new(id, id, "", "", Capability.None, [], [], enabledByDefault);

    private AppSettingsStore Store() => new(_root.Paths, NullLogger.Instance);

    [Fact]
    public void 没有文件_按清单的默认启用状态()
    {
        var catalog = new[] { Manifest("a"), Manifest("b", enabledByDefault: false) };

        var enabled = Store().Load().ResolveEnabled(catalog);

        Assert.Equal(["a"], enabled.Select(i => i.Value));
    }

    [Fact]
    public void 用户的明确选择优先于默认值()
    {
        var catalog = new[] { Manifest("a"), Manifest("b", enabledByDefault: false) };
        var settings = new AppSettings { Features = { ["a"] = false, ["b"] = true } };

        Assert.Equal(["b"], settings.ResolveEnabled(catalog).Select(i => i.Value));
    }

    [Fact]
    public void 新增的默认启用功能_老用户升级后自动启用()
    {
        var settings = new AppSettings { Features = { ["a"] = false } }; // 只记录过 a
        var catalog = new[] { Manifest("a"), Manifest("newfeature") };

        Assert.Equal(["newfeature"], settings.ResolveEnabled(catalog).Select(i => i.Value));
    }

    [Fact]
    public void 配置里不存在的功能id_被忽略()
    {
        var settings = new AppSettings { Features = { ["ghost"] = true, ["Bad Id!"] = true } };

        var enabled = settings.ResolveEnabled([Manifest("a")]);

        Assert.Equal(["a"], enabled.Select(i => i.Value));
    }

    [Fact]
    public async Task 保存后能读回()
    {
        await Store().SaveAsync(new AppSettings { Features = { ["a"] = false } });

        var loaded = Store().Load();

        Assert.False(loaded.Features["a"]);
    }

    [Fact]
    public void 文件损坏_备份并用默认值()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.AppSettingsFile, "{ not json");

        var loaded = Store().Load();

        Assert.Empty(loaded.Features);
        Assert.Single(Directory.GetFiles(_root.Paths.ConfigDirectory, "app.json.bad-*"));
    }

    [Fact]
    public void features写成null_不会留下空引用()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.AppSettingsFile, """{"features": null}""");

        var loaded = Store().Load();

        Assert.NotNull(loaded.Features);
        Assert.Equal(["a"], loaded.ResolveEnabled([Manifest("a")]).Select(i => i.Value));
    }

    [Fact]
    public void appearance写成null_读成空的外观设置()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.AppSettingsFile, """{"appearance": null}""");

        var loaded = Store().Load();

        Assert.NotNull(loaded.Appearance);
        Assert.Null(loaded.Appearance.Preset);
    }

    [Fact]
    public async Task 外观设置_保存后能读回()
    {
        var store = Store();
        var settings = store.Load();
        settings.Appearance = new AppearanceSettings { BlurRadius = 40, Saturation = 1.5, TintOpacity = 0.3, Accent = "#9B8CFF", Corners = "extraRounded" };
        await store.SaveAsync(settings);

        var loaded = store.Load().Appearance;
        Assert.Equal(40, loaded.BlurRadius);
        Assert.Equal(1.5, loaded.Saturation);
        Assert.Equal(0.3, loaded.TintOpacity);
        Assert.Equal("#9B8CFF", loaded.Accent);
        Assert.Equal("extraRounded", loaded.Corners);
        Assert.Null(loaded.Tint);
    }
}

public class AutostartTaskTests
{
    [Fact]
    public void ProgramFiles下的路径_视为安全()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(AutostartTask.IsSafeInstallPath(Path.Combine(programFiles, "WAIO", "Waio.exe")));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Waio.exe")]
    [InlineData(@"D:\MyProjects\WAIO\src\Waio.App\bin\Debug\Waio.exe")]
    public void 用户可写位置_视为不安全(string path)
    {
        Assert.False(AutostartTask.IsSafeInstallPath(path));
    }

    [Fact]
    public void ProgramFiles的同名前缀目录_不被误判()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // "C:\Program Files Evil\x.exe" 以 "C:\Program Files" 开头,但不在其下
        Assert.False(AutostartTask.IsSafeInstallPath(programFiles + @" Evil\Waio.exe"));
    }

    [Fact]
    public void 路径用点点回溯逃出ProgramFiles_视为不安全()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.False(AutostartTask.IsSafeInstallPath(Path.Combine(programFiles, "..", "Users", "Public", "Waio.exe")));
    }

    [Fact]
    public void 任务XML_是合法XML且包含最高权限和无时间限制()
    {
        var xml = AutostartTask.BuildTaskXml(@"C:\Program Files\WAIO\Waio.exe", @"PC\用户");

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT0S", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal(@"C:\Program Files\WAIO\Waio.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--autostart", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"PC\用户", doc.Descendants(ns + "UserId").First().Value);
    }

    [Fact]
    public void 任务XML_路径和用户名里的特殊字符被转义_不能注入标签()
    {
        var xml = AutostartTask.BuildTaskXml(@"C:\a&b\<x>.exe", "u</UserId><Evil/>");

        var doc = XDocument.Parse(xml); // 若没转义会解析失败或多出 Evil 元素
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "Evil");
        Assert.Equal(@"C:\a&b\<x>.exe", doc.Descendants().Single(e => e.Name.LocalName == "Command").Value);
    }
}
