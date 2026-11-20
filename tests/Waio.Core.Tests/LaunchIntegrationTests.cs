using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Core.Host;
using Waio.Core.Launch;
using Waio.Kit.Services;

namespace Waio.Core.Tests;

/// <summary>
/// 验证 ADR-0006 最关键的安全属性:提权进程里默认启动的子进程**确实是降权的**。
/// 做法:启动一个隐藏的 PowerShell,让它把"自己是不是管理员"写进临时文件。
/// 会创建真实子进程(可能一闪而过控制台窗口),所以和其他集成测试一样需要 WAIO_INTEGRATION=1。
/// 测试进程不是管理员时降权没有意义,直接跳过。
/// </summary>
public class LaunchIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    [IntegrationFact]
    public async Task 提权进程中_默认启动的子进程是降权的()
    {
        if (!WindowsCapabilityProbe.IsElevated())
        {
            output.WriteLine("测试进程不是管理员,降权无从验证,已跳过(请在管理员终端运行)");
            return;
        }

        if (!UacStatus.IsEnabled())
        {
            // 这台机器 UAC 被关闭:资源管理器本身就是完整管理员令牌,没有"普通令牌"可降。降权无法在此验证。
            output.WriteLine("⚠️ 系统 UAC 已关闭(EnableLUA=0),降权无法验证——需要在 UAC 开启的机器上重跑。本测试未验证任何东西。");
            return;
        }

        var answer = await RunChildAndGetIsAdminAsync(runElevated: false);

        Assert.Equal("False", answer);
    }

    [IntegrationFact]
    public async Task RunElevated为true_子进程保持管理员权限_作为对照()
    {
        if (!WindowsCapabilityProbe.IsElevated())
        {
            output.WriteLine("测试进程不是管理员,已跳过");
            return;
        }

        var answer = await RunChildAndGetIsAdminAsync(runElevated: true);

        Assert.Equal("True", answer);
    }

    private async Task<string> RunChildAndGetIsAdminAsync(bool runElevated)
    {
        var resultFile = Path.Combine(_root.Path, $"result-{runElevated}.txt");
        var script =
            "$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent());" +
            $"$p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator).ToString() | Out-File -Encoding ascii -NoNewline '{resultFile}'";
        // EncodedCommand 避免命令行里嵌套引号的转义问题
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var launcher = new ShellLauncher(NullLogger.Instance);
        await launcher.LaunchAsync(new LaunchRequest(
            PowerShell,
            $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            RunElevated: runElevated));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultFile))
            {
                await Task.Delay(100); // 等它写完并关闭文件
                return (await File.ReadAllTextAsync(resultFile)).Trim();
            }

            await Task.Delay(100);
        }

        Assert.Fail("20 秒内子进程没有写出结果文件");
        return "";
    }
}
