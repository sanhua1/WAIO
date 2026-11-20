namespace Waio.Architecture.Tests;

/// <summary>架构测试依赖的扫描器本身也要被测:否则"没有违规"可能只是因为扫描器坏了。</summary>
public class SourceScannerTests
{
    [Fact]
    public void 命中代码行里的禁用标记并给出行号()
    {
        string[] lines =
        [
            "var a = 1;",
            "NativeMethods.SetWindowsHookEx(13, proc, 0, 0);",
        ];

        var result = SourceScanner.FindTokens("x.cs", lines, ["SetWindowsHookEx"]);

        var v = Assert.Single(result);
        Assert.Equal(2, v.Line);
        Assert.Equal("SetWindowsHookEx", v.Token);
    }

    [Theory]
    [InlineData("// SetWindowsHookEx 只能在 Core 里调用")]
    [InlineData("/// 不得直接调用 SetWindowsHookEx")]
    [InlineData("    * SetWindowsHookEx")]
    [InlineData("/* SetWindowsHookEx */")]
    public void 注释行不算违规(string line)
    {
        Assert.Empty(SourceScanner.FindTokens("x.cs", [line], ["SetWindowsHookEx"]));
    }

    [Fact]
    public void 依赖规则_功能不能引用Core和其他功能()
    {
        var allowed = ArchitectureRules.AllowedReferences("Waio.Feature.Clipboard")!;

        Assert.True(allowed("Waio.Kit"));
        Assert.True(allowed("Waio.UI"));
        Assert.False(allowed("Waio.Core"));
        Assert.False(allowed("Waio.Shell"));
        Assert.False(allowed("Waio.Feature.Translate"));
    }

    [Fact]
    public void 依赖规则_Kit不能引用任何WAIO项目()
    {
        var allowed = ArchitectureRules.AllowedReferences("Waio.Kit")!;

        Assert.False(allowed("Waio.Core"));
        Assert.False(allowed("Waio.UI"));
    }

    [Fact]
    public void 依赖规则_未登记项目返回null()
    {
        Assert.Null(ArchitectureRules.AllowedReferences("Waio.Mystery"));
    }
}
