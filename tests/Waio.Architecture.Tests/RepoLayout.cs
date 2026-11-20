using System.Xml.Linq;

namespace Waio.Architecture.Tests;

/// <summary>定位仓库并枚举 src 下的项目与源码。</summary>
internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    public static IReadOnlyList<ProjectInfo> Projects { get; } =
        Directory.EnumerateFiles(Src, "*.csproj", SearchOption.AllDirectories)
            .Select(ProjectInfo.Load)
            .OrderBy(p => p.Name)
            .ToList();

    public static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f));

    private static bool IsBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin") || parts.Contains("obj");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Waio.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录(Waio.sln)");
    }
}

internal sealed record ProjectInfo(
    string Name,
    string Directory,
    string? TargetFramework,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences)
{
    public static ProjectInfo Load(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        var name = Path.GetFileNameWithoutExtension(csprojPath);

        var tfm = doc.Descendants("TargetFramework").Select(e => e.Value.Trim()).FirstOrDefault();
        var projectRefs = doc.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string?)e.Attribute("Include") ?? string.Empty))
            .ToList();
        var packageRefs = doc.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToList();

        return new ProjectInfo(name, Path.GetDirectoryName(csprojPath)!, tfm, projectRefs, packageRefs);
    }
}
