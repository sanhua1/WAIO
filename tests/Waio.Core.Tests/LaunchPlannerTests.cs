using Waio.Core.Launch;
using Waio.Kit.Services;

namespace Waio.Core.Tests;

/// <summary>启动请求来自可被篡改的配置,校验逻辑必须严格(ADR-0004、ADR-0006)。</summary>
public class LaunchPlannerTests : IDisposable
{
    private const string Explorer = @"C:\Windows\explorer.exe";

    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root.Path, name);
        File.WriteAllText(path, "");
        return path;
    }

    private static LaunchPlan Plan(string target, string? args = null, string? workingDirectory = null) =>
        LaunchPlanner.Plan(new LaunchRequest(target, args, workingDirectory), Explorer);

    [Fact]
    public void 裸命令名_交给系统按PATH搜索()
    {
        var plan = Plan("notepad", "a.txt");

        Assert.Null(plan.ApplicationName);
        Assert.Equal("notepad a.txt", plan.CommandLine);
    }

    [Fact]
    public void 可执行文件_带引号的完整路径加参数()
    {
        var exe = CreateFile("tool.exe");

        var plan = Plan(exe, "--flag \"x y\"");

        Assert.Equal(exe, plan.ApplicationName);
        Assert.Equal($"\"{exe}\" --flag \"x y\"", plan.CommandLine);
        Assert.Equal(_root.Path, plan.WorkingDirectory); // 默认工作目录是程序所在目录
    }

    [Fact]
    public void 文档_通过资源管理器打开()
    {
        var doc = CreateFile("note.txt");

        var plan = Plan(doc);

        Assert.Equal(Explorer, plan.ApplicationName);
        Assert.Equal($"\"{Explorer}\" \"{doc}\"", plan.CommandLine);
    }

    [Fact]
    public void 文件夹_通过资源管理器打开_即使名字像exe()
    {
        var folder = Path.Combine(_root.Path, "weird.exe");
        Directory.CreateDirectory(folder);

        var plan = Plan(folder);

        Assert.Equal(Explorer, plan.ApplicationName);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ms-settings:display")]
    public void 允许的链接_通过资源管理器打开(string url)
    {
        var plan = Plan(url);

        Assert.Equal(Explorer, plan.ApplicationName);
        Assert.Equal($"\"{Explorer}\" \"{url}\"", plan.CommandLine);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=x")]
    public void 其他scheme_拒绝(string url)
    {
        var ex = Assert.Throws<LaunchException>(() => Plan(url));

        Assert.Contains("不支持的链接类型", ex.Message);
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:appsfolder\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe")]
    public void 开始菜单所有应用里的条目_通过资源管理器打开(string target)
    {
        var plan = Plan(target);

        Assert.Equal(Explorer, plan.ApplicationName);
        Assert.Equal($"\"{Explorer}\" \"{target}\"", plan.CommandLine);
    }

    [Theory]
    [InlineData("shell:startup")]
    [InlineData(@"shell:AppsFolder\")]
    [InlineData("shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}")]
    public void shell的其他位置_拒绝(string target)
    {
        var ex = Assert.Throws<LaunchException>(() => Plan(target));

        Assert.Contains("不支持的链接类型", ex.Message);
    }

    [Fact]
    public void 开始菜单条目带参数_拒绝() =>
        Assert.Throws<LaunchException>(() => Plan(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "-x"));

    [Fact]
    public void 盘符路径不会被当成scheme()
    {
        var doc = CreateFile("a.txt");

        var plan = Plan(doc); // 不抛"不支持的链接类型"即可

        Assert.Equal(doc, plan.Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空目标_拒绝(string target)
    {
        Assert.Throws<LaunchException>(() => Plan(target));
    }

    [Fact]
    public void 目标含引号或NUL_拒绝_防止命令行注入()
    {
        Assert.Throws<LaunchException>(() => Plan("C:\\x\\a\" --evil \"b.exe"));
        Assert.Throws<LaunchException>(() => Plan("notepad\0.exe"));
    }

    [Fact]
    public void 过长的目标_拒绝()
    {
        Assert.Throws<LaunchException>(() => Plan(new string('a', 5000)));
    }

    [Fact]
    public void 文件不存在_拒绝并说明()
    {
        var ex = Assert.Throws<LaunchException>(() => Plan(Path.Combine(_root.Path, "missing.exe")));

        Assert.Contains("找不到", ex.Message);
    }

    [Fact]
    public void 相对路径_拒绝_要求完整路径()
    {
        Assert.Throws<LaunchException>(() => Plan(@"tools\a.exe"));
    }

    [Fact]
    public void 文档带参数_拒绝_因为不支持()
    {
        var doc = CreateFile("a.txt");

        Assert.Throws<LaunchException>(() => Plan(doc, "--x"));
        Assert.Throws<LaunchException>(() => Plan("https://example.com", "--x"));
    }

    [Fact]
    public void 工作目录不存在_拒绝()
    {
        Assert.Throws<LaunchException>(() => Plan("notepad", null, Path.Combine(_root.Path, "nope")));
    }

    [Fact]
    public void 展开环境变量()
    {
        Environment.SetEnvironmentVariable("WAIO_TEST_DIR", _root.Path);
        var exe = CreateFile("env.exe");

        var plan = Plan(@"%WAIO_TEST_DIR%\env.exe");

        Assert.Equal(exe, plan.ApplicationName);
    }

    [Fact]
    public void 参数含NUL_拒绝()
    {
        Assert.Throws<LaunchException>(() => Plan("notepad", "a\0b"));
    }
}
