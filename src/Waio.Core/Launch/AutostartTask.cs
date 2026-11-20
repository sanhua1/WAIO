using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Waio.Core.Host;

namespace Waio.Core.Launch;

public sealed record AutostartResult(bool Ok, string Message);

/// <summary>
/// 开机自启 = 一个"登录时触发、最高权限运行"的计划任务(ADR-0004 §1):免每次开机弹 UAC。
/// 创建/删除任务需要管理员权限;查询不需要。
/// <para>
/// ⚠️ 安全:计划任务会以管理员权限执行 <c>exePath</c>。若该路径普通进程可写,
/// 任何普通进程都能替换 exe 来获得管理员执行权。因此默认只允许安装在 Program Files 之下(<see cref="IsSafeInstallPath"/>),
/// 开发调试需要显式放行。
/// </para>
/// </summary>
public sealed class AutostartTask(ILogger logger)
{
    public const string TaskName = "WAIO";
    public const string AutostartArgument = "--autostart";

    /// <summary>exe 是否位于普通用户无法写入的受信任位置(Program Files)。</summary>
    public static bool IsSafeInstallPath(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        return roots.Any(root =>
            root.Length > 0 && full.StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildTaskXml(string exePath, string userName)
    {
        var user = SecurityElement.Escape(userName);
        var command = SecurityElement.Escape(exePath);
        var directory = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>WAIO 登录时自动启动(最高权限,免 UAC)</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{command}</Command><Arguments>{AutostartArgument}</Arguments><WorkingDirectory>{directory}</WorkingDirectory></Exec>
              </Actions>
            </Task>
            """;
    }

    public bool IsInstalled() => RunSchtasks($"/Query /TN {TaskName}").ExitCode == 0;

    /// <param name="allowUnsafePath">允许 exe 在用户可写位置(仅开发调试)。</param>
    public AutostartResult Install(string exePath, bool allowUnsafePath = false)
    {
        if (!WindowsCapabilityProbe.IsElevated())
        {
            return new AutostartResult(false, "需要管理员权限才能创建开机自启任务");
        }

        if (!allowUnsafePath && !IsSafeInstallPath(exePath))
        {
            return new AutostartResult(false, "程序不在 Program Files 下:以管理员权限自启动一个普通用户可替换的文件有提权风险,已拒绝");
        }

        var xml = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(BuildTaskXml(exePath, WindowsIdentity.GetCurrent().Name))).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"waio-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllBytes(path, xml);

            // 防替换:schtasks 将以管理员身份读这个文件,而它在普通进程可写的目录里。
            // 重新以"拒绝他人写入/删除"的方式打开并校验内容,再运行——持有句柄期间无法被替换。
            using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var onDisk = new byte[guard.Length];
            guard.ReadExactly(onDisk);
            if (!onDisk.AsSpan().SequenceEqual(xml))
            {
                return new AutostartResult(false, "任务定义文件在写入后被改动,已中止");
            }

            var (exitCode, output) = RunSchtasks($"/Create /TN {TaskName} /XML \"{path}\" /F");
            logger.LogInformation("创建开机自启任务,退出码 {ExitCode}", exitCode);
            return exitCode == 0
                ? new AutostartResult(true, "已启用开机自启")
                : new AutostartResult(false, $"创建任务失败:{output.Trim()}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 残留的临时文件无害
            }
        }
    }

    public AutostartResult Uninstall()
    {
        if (!WindowsCapabilityProbe.IsElevated())
        {
            return new AutostartResult(false, "需要管理员权限才能删除开机自启任务");
        }

        var (exitCode, output) = RunSchtasks($"/Delete /TN {TaskName} /F");
        return exitCode == 0
            ? new AutostartResult(true, "已关闭开机自启")
            : new AutostartResult(false, $"删除任务失败:{output.Trim()}");
    }

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        var startInfo = new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 schtasks.exe");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            process.Kill();
            return (-1, "schtasks 超时");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
