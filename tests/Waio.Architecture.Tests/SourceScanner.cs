namespace Waio.Architecture.Tests;

internal sealed record Violation(string File, int Line, string Token);

/// <summary>在源码文本里查找禁用标记。注释行(//、///、*)不算,避免文档里提到 API 名字时误报。</summary>
internal static class SourceScanner
{
    public static IReadOnlyList<Violation> FindTokens(string fileLabel, IEnumerable<string> lines, IReadOnlyList<string> forbiddenTokens)
    {
        var result = new List<Violation>();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in forbiddenTokens)
            {
                if (line.Contains(token, StringComparison.Ordinal))
                {
                    result.Add(new Violation(fileLabel, lineNumber, token));
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<Violation> ScanFiles(IEnumerable<string> files, IReadOnlyList<string> forbiddenTokens) =>
        files.SelectMany(f => FindTokens(Path.GetRelativePath(RepoLayout.Root, f), File.ReadLines(f), forbiddenTokens)).ToList();

    public static string Format(IEnumerable<Violation> violations) =>
        string.Join(Environment.NewLine, violations.Select(v => $"  {v.File}:{v.Line}  包含禁用标记 '{v.Token}'"));
}
