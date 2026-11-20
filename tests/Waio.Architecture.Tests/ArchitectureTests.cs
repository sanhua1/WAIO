namespace Waio.Architecture.Tests;

public class ArchitectureTests
{
    [Fact]
    public void 每个项目的依赖符合分层规则()
    {
        var errors = new List<string>();
        foreach (var project in RepoLayout.Projects)
        {
            var allowed = ArchitectureRules.AllowedReferences(project.Name);
            if (allowed is null)
            {
                errors.Add($"{project.Name}:未登记的项目。先在 ArchitectureRules 里声明它的依赖规则(并发 ADR)。");
                continue;
            }

            errors.AddRange(project.ProjectReferences
                .Where(r => !allowed(r))
                .Select(r => $"{project.Name} 不允许引用 {r}"));
        }

        Assert.True(errors.Count == 0, "分层规则被破坏:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Kit_保持平台无关且依赖最小()
    {
        var kit = RepoLayout.Projects.Single(p => p.Name == ArchitectureRules.Kit);

        Assert.Equal("$(WaioTfm)", kit.TargetFramework); // 不得使用 $(WaioWinTfm) 或写死 Windows TFM
        var extra = kit.PackageReferences.Except(ArchitectureRules.KitPackageAllowlist).ToList();
        Assert.True(extra.Count == 0, "Kit 引入了白名单外的 NuGet 包:" + string.Join(", ", extra));
    }

    [Fact]
    public void 输入钩子API只能出现在Core的Input目录()
    {
        var inputDir = Path.Combine(RepoLayout.Src, "Waio.Core", "Input") + Path.DirectorySeparatorChar;
        var files = RepoLayout.SourceFiles(RepoLayout.Src)
            .Where(f => !f.StartsWith(inputDir, StringComparison.OrdinalIgnoreCase));

        var violations = SourceScanner.ScanFiles(files, ArchitectureRules.InputEngineOnlyTokens);

        Assert.True(violations.Count == 0,
            "只有 Waio.Core/Input 允许安装钩子/发送按键,其他地方请通过 IInputService:" + Environment.NewLine + SourceScanner.Format(violations));
    }

    [Fact]
    public void 创建进程的API只能出现在Core的Launch目录()
    {
        var launchDir = Path.Combine(RepoLayout.Src, "Waio.Core", "Launch") + Path.DirectorySeparatorChar;
        var files = RepoLayout.SourceFiles(RepoLayout.Src)
            .Where(f => !f.StartsWith(launchDir, StringComparison.OrdinalIgnoreCase));

        var violations = SourceScanner.ScanFiles(files, ArchitectureRules.ProcessCreationOnlyTokens);

        Assert.True(violations.Count == 0,
            "只有 Waio.Core/Launch 允许创建进程(默认降权,ADR-0006),其他地方请通过 ILauncher:" + Environment.NewLine + SourceScanner.Format(violations));
    }

    [Fact]
    public void 功能代码不得绕过context取系统路径()
    {
        var featuresDir = Path.Combine(RepoLayout.Src, "Features");
        var violations = SourceScanner.ScanFiles(RepoLayout.SourceFiles(featuresDir), ArchitectureRules.FeatureForbiddenTokens);

        Assert.True(violations.Count == 0,
            "功能请使用 IFeatureStorage 等 context 服务:" + Environment.NewLine + SourceScanner.Format(violations));
    }

    [Fact]
    public void Realtime目录不含UI线程API和异步()
    {
        var realtimeFiles = Directory.EnumerateDirectories(RepoLayout.Src, "Realtime", SearchOption.AllDirectories)
            .SelectMany(RepoLayout.SourceFiles);

        var violations = SourceScanner.ScanFiles(realtimeFiles, ArchitectureRules.RealtimeForbiddenTokens);

        Assert.True(violations.Count == 0,
            "Realtime/ 在钩子线程同步运行,不得有 UI 线程 API、async/await 或阻塞调用:" + Environment.NewLine + SourceScanner.Format(violations));
    }

    [Fact]
    public void 源文件不超过行数上限()
    {
        var tooLong = RepoLayout.SourceFiles(RepoLayout.Src)
            .Select(f => (File: Path.GetRelativePath(RepoLayout.Root, f), Lines: File.ReadLines(f).Count()))
            .Where(x => x.Lines > ArchitectureRules.MaxFileLines)
            .Select(x => $"  {x.File}:{x.Lines} 行")
            .ToList();

        Assert.True(tooLong.Count == 0,
            $"单个文件不得超过 {ArchitectureRules.MaxFileLines} 行,请拆分:" + Environment.NewLine + string.Join(Environment.NewLine, tooLong));
    }

    [Fact]
    public void 每个功能目录都有README()
    {
        var missing = RepoLayout.Projects
            .Where(p => p.Name.StartsWith(ArchitectureRules.FeaturePrefix, StringComparison.Ordinal))
            .Where(p => !File.Exists(Path.Combine(p.Directory, "README.md")))
            .Select(p => p.Name)
            .ToList();

        Assert.True(missing.Count == 0, "这些功能缺少 README.md(用途、命令、设置、所需能力):" + string.Join(", ", missing));
    }
}
