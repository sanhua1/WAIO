using Waio.Kit.Services;

namespace Waio.Core.Launch;

/// <summary>
/// 决定"怎么启动"。
/// <see cref="ApplicationName"/>/<see cref="CommandLine"/> 用于降权路径(CreateProcessWithTokenW):
/// ApplicationName 为 null 表示让系统按 PATH 搜索命令行里的第一个词。
/// <see cref="Target"/>/<see cref="Arguments"/> 是校验、展开环境变量后的原始目标,用于不降权路径(ShellExecute)。
/// </summary>
internal sealed record LaunchPlan(
    string? ApplicationName, string CommandLine, string? WorkingDirectory, string Target, string? Arguments);

/// <summary>
/// 把用户配置的 <see cref="LaunchRequest"/> 变成具体的启动计划,并做校验。
/// 纯逻辑(只读文件系统),可完整单测。<b>请求来自可被普通进程篡改的配置,按不可信输入处理</b>(ADR-0004)。
/// </summary>
internal static class LaunchPlanner
{
    private const int MaxLength = 4096;

    // 只允许这些 scheme,避免配置里写 file:/javascript:/自定义协议等去触发意外行为
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "ms-settings",
    };

    /// <summary>
    /// 开始菜单"所有应用"里的条目(传统程序和商店应用都在这里),启动台用它打开应用(ADR-0023)。
    /// <c>shell:</c> 的其他位置(启动文件夹、控制面板 CLSID 等)仍然拒绝。
    /// </summary>
    private const string AppsFolderPrefix = @"shell:AppsFolder\";

    public static LaunchPlan Plan(LaunchRequest request, string explorerPath)
    {
        var target = Clean(request.Target, "目标");
        var arguments = request.Arguments;
        if (arguments is not null && (arguments.Contains('\0') || arguments.Length > MaxLength))
        {
            throw new LaunchException("参数无效");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? null
            : Clean(request.WorkingDirectory, "工作目录");
        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
        {
            throw new LaunchException($"工作目录不存在:{workingDirectory}");
        }

        if (TryGetScheme(target, out var scheme))
        {
            var isAppsFolderItem = target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
                && target.Length > AppsFolderPrefix.Length;
            if (!AllowedSchemes.Contains(scheme) && !isAppsFolderItem)
            {
                throw new LaunchException($"不支持的链接类型:{scheme}:");
            }

            return ViaExplorer(explorerPath, target, arguments, workingDirectory);
        }

        var hasDirectory = target.AsSpan().IndexOfAny('\\', '/') >= 0;
        var extension = Path.GetExtension(target);

        if (!hasDirectory && extension.Length == 0)
        {
            // 裸命令名(如 notepad):让系统按 PATH 搜索
            return new LaunchPlan(null, Join(target, arguments), workingDirectory, target, arguments);
        }

        if (!Path.IsPathRooted(target))
        {
            throw new LaunchException($"请使用完整路径:{target}");
        }

        var isDirectory = Directory.Exists(target);
        if (!isDirectory && !File.Exists(target))
        {
            throw new LaunchException($"找不到:{target}");
        }

        var isExecutable = !isDirectory
            && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".com", StringComparison.OrdinalIgnoreCase));
        if (isExecutable)
        {
            return new LaunchPlan(
                target, Join($"\"{target}\"", arguments), workingDirectory ?? Path.GetDirectoryName(target), target, arguments);
        }

        return ViaExplorer(explorerPath, target, arguments, workingDirectory);
    }

    private static LaunchPlan ViaExplorer(string explorerPath, string target, string? arguments, string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            throw new LaunchException("文档、文件夹和链接不支持命令行参数");
        }

        return new LaunchPlan(explorerPath, $"\"{explorerPath}\" \"{target}\"", workingDirectory, target, null);
    }

    private static string Join(string program, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? program : $"{program} {arguments}";

    private static string Clean(string? value, string what)
    {
        // 展开前先查 NUL:ExpandEnvironmentVariables 是原生 API,会在 NUL 处悄悄截断字符串
        if (value is not null && value.Contains('\0'))
        {
            throw new LaunchException($"{what}包含无效字符");
        }

        var text = Environment.ExpandEnvironmentVariables(value?.Trim() ?? "");
        if (text.Length == 0)
        {
            throw new LaunchException($"{what}不能为空");
        }

        if (text.Length > MaxLength || text.Contains('\0') || text.Contains('"'))
        {
            throw new LaunchException($"{what}包含无效字符或过长");
        }

        return text;
    }

    /// <summary>形如 "https:..." 的 scheme;盘符 "C:\..." 不算(scheme 至少两个字符)。</summary>
    private static bool TryGetScheme(string target, out string scheme)
    {
        scheme = "";
        var colon = target.IndexOf(':');
        if (colon < 2)
        {
            return false;
        }

        var candidate = target[..colon];
        if (!candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return false;
        }

        scheme = candidate;
        return true;
    }
}
