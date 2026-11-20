using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Storage;
using Waio.Kit.Features;

namespace Waio.Core.Tests;

public class SettingsTests
{
    public sealed class SampleSettings
    {
        public int SchemaVersion { get; set; } = 1;

        public string Name { get; set; } = "默认";

        public List<int> Numbers { get; set; } = [];
    }

    private static FeatureSettings Create(TempRoot root, string id = "sample") =>
        new(id, root.Paths, NullLogger.Instance);

    [Fact]
    public void 文件不存在_返回默认值()
    {
        using var root = new TempRoot();

        var settings = Create(root).Get<SampleSettings>();

        Assert.Equal("默认", settings.Name);
    }

    [Fact]
    public async Task 保存后能读回_并且重启后仍在()
    {
        using var root = new TempRoot();
        await Create(root).SaveAsync(new SampleSettings { Name = "新名字", Numbers = [1, 2, 3] });

        var reloaded = Create(root).Get<SampleSettings>(); // 全新实例 = 模拟重启

        Assert.Equal("新名字", reloaded.Name);
        Assert.Equal([1, 2, 3], reloaded.Numbers);
    }

    [Fact]
    public async Task Get返回独立副本_改了不保存不影响别人()
    {
        using var root = new TempRoot();
        var settings = Create(root);
        await settings.SaveAsync(new SampleSettings { Name = "原值" });

        var copy = settings.Get<SampleSettings>();
        copy.Name = "被偷偷改了";

        Assert.Equal("原值", settings.Get<SampleSettings>().Name);
    }

    [Fact]
    public async Task 保存触发Changed事件()
    {
        using var root = new TempRoot();
        var settings = Create(root);
        var count = 0;
        settings.Changed += () => count++;

        await settings.SaveAsync(new SampleSettings());
        await settings.SaveAsync(new SampleSettings());

        Assert.Equal(2, count);
    }

    [Fact]
    public void 文件损坏_备份后用默认值_不崩溃()
    {
        using var root = new TempRoot();
        var path = root.Paths.FeatureSettingsFile("sample");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ 这不是合法的 json ");

        var settings = Create(root).Get<SampleSettings>();

        Assert.Equal("默认", settings.Name);
        Assert.False(File.Exists(path)); // 原文件被移走
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "sample.json.bad-*"));
    }

    [Fact]
    public async Task 原子写入_不留下临时文件()
    {
        using var root = new TempRoot();
        await Create(root).SaveAsync(new SampleSettings());

        var dir = Path.GetDirectoryName(root.Paths.FeatureSettingsFile("sample"))!;
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public async Task 不同功能的设置互相隔离()
    {
        using var root = new TempRoot();
        var hub = new SettingsHub(root.Paths, NullLoggerFactory.Instance);
        await hub.ForFeature("alpha").SaveAsync(new SampleSettings { Name = "A" });

        Assert.Equal("默认", hub.ForFeature("beta").Get<SampleSettings>().Name);
        Assert.Same(hub.ForFeature("alpha"), hub.ForFeature("alpha")); // 同一功能同一实例
    }

    [Fact]
    public void 旧文件缺少新字段_用默认值补齐()
    {
        using var root = new TempRoot();
        var path = root.Paths.FeatureSettingsFile("sample");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "schemaVersion": 1, "name": "只有名字" }""");

        var settings = Create(root).Get<SampleSettings>();

        Assert.Equal("只有名字", settings.Name);
        Assert.Empty(settings.Numbers);
    }

    [Fact]
    public void FeatureStorage_创建并限定在功能目录()
    {
        using var root = new TempRoot();

        var storage = new FeatureStorage(new FeatureId("clipboard"), root.Paths);

        Assert.True(Directory.Exists(storage.DataDirectory));
        Assert.EndsWith(Path.Combine("data", "clipboard"), storage.DataDirectory);
    }
}
