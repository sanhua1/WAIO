using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Waio.Core.Host;
using Waio.Kit.Services;

namespace Waio.Core.Launch;

/// <summary>
/// 全仓库唯一的"启动外部程序"实现(ADR-0006)。WAIO 常驻管理员,默认借用资源管理器的普通令牌降权启动;
/// 取不到该令牌时<b>失败而不是回退成高权限</b>。
/// 做法参考 Raymond Chen《How do I launch an unelevated process from my elevated process》(取 shell 进程令牌 + CreateProcessWithTokenW)。
/// </summary>
public sealed class ShellLauncher : ILauncher
{
    private readonly ILogger _logger;
    private readonly string _explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private readonly Func<bool> _isElevated;

    public ShellLauncher(ILogger logger)
        : this(logger, WindowsCapabilityProbe.IsElevated)
    {
    }

    internal ShellLauncher(ILogger logger, Func<bool> isElevated)
    {
        _logger = logger;
        _isElevated = isElevated;
    }

    public ValueTask LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = LaunchPlanner.Plan(request, _explorerPath); // 校验在调用线程同步完成,错误立刻抛给调用方

        // 进程本身不是管理员(开发调试)时降权没有意义;RunElevated 是用户显式选择保持高权限
        var deElevate = _isElevated() && !request.RunElevated;
        _logger.LogInformation("启动 {Target}(降权={DeElevate})", plan.Target, deElevate);

        Action launch = deElevate ? () => LaunchWithShellToken(plan, _logger) : () => LaunchDirect(plan);
        return new ValueTask(Task.Run(launch, cancellationToken));
    }

    private static void LaunchDirect(LaunchPlan plan)
    {
        try
        {
            var startInfo = new ProcessStartInfo(plan.Target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(plan.Arguments))
            {
                startInfo.Arguments = plan.Arguments;
            }

            if (plan.WorkingDirectory is not null)
            {
                startInfo.WorkingDirectory = plan.WorkingDirectory;
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            throw new LaunchException($"无法启动:{ex.Message}", ex);
        }
    }

    private static void LaunchWithShellToken(LaunchPlan plan, ILogger logger)
    {
        var token = GetShellToken();
        try
        {
            if (IsElevated(token))
            {
                // 常见原因:UAC 被关闭,资源管理器本身就是完整管理员令牌。此时没有普通令牌可降,子进程会以管理员运行。
                // 整个系统本来就全是管理员,不构成"借 WAIO 提权",但不能假装降权成功。
                logger.LogWarning("桌面外壳的令牌本身是提权的(UAC 可能已关闭),无法降权,{Target} 将以管理员权限运行", plan.Target);
            }

            var startup = new LaunchNative.StartupInfo { Cb = (uint)Marshal.SizeOf<LaunchNative.StartupInfo>() };
            var commandLine = new StringBuilder(plan.CommandLine, Math.Max(plan.CommandLine.Length + 1, 260));
            if (!LaunchNative.CreateProcessWithTokenW(
                    token, 0, plan.ApplicationName, commandLine, LaunchNative.CreateUnicodeEnvironment,
                    0, plan.WorkingDirectory, ref startup, out var info))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new LaunchException($"无法启动:{error.Message}", error);
            }

            LaunchNative.CloseHandle(info.Process);
            LaunchNative.CloseHandle(info.Thread);
        }
        finally
        {
            LaunchNative.CloseHandle(token);
        }
    }

    private static bool IsElevated(nint token) =>
        LaunchNative.GetTokenInformation(token, LaunchNative.TokenElevationClass, out var elevated, sizeof(int), out _) && elevated != 0;

    /// <summary>复制资源管理器进程的主令牌(普通权限)。拿不到就抛异常,绝不回退。</summary>
    private static nint GetShellToken()
    {
        var shell = LaunchNative.GetShellWindow();
        if (shell == 0 || LaunchNative.GetWindowThreadProcessId(shell, out var pid) == 0)
        {
            throw new LaunchException("找不到桌面外壳(资源管理器),无法以普通权限启动");
        }

        var process = LaunchNative.OpenProcess(LaunchNative.ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            throw new LaunchException("无法访问桌面外壳进程,无法以普通权限启动", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            if (!LaunchNative.OpenProcessToken(process, LaunchNative.TokenDuplicate, out var source))
            {
                throw new LaunchException("无法读取桌面外壳的令牌,无法以普通权限启动", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                if (!LaunchNative.DuplicateTokenEx(
                        source, LaunchNative.TokenAllAccess, 0, LaunchNative.SecurityImpersonation, LaunchNative.TokenPrimary, out var primary))
                {
                    throw new LaunchException("无法复制桌面外壳的令牌,无法以普通权限启动", new Win32Exception(Marshal.GetLastWin32Error()));
                }

                return primary;
            }
            finally
            {
                LaunchNative.CloseHandle(source);
            }
        }
        finally
        {
            LaunchNative.CloseHandle(process);
        }
    }
}
