using System.Runtime.InteropServices;
using Waio.Core.Host;
using Waio.Core.Launch;
using Waio.Core.Platform;

namespace Waio.Shell.Hosting;

/// <summary>
/// 进程启动阶段的决策:单实例 + 提权重启(ADR-0004 §4)。放在 Shell 而不是 App,是因为 App 不允许引用 Core。
/// <list type="bullet">
/// <item>已有实例在运行 → 通知它"显示设置"后本进程退出(<see cref="ShouldRun"/> = false);</item>
/// <item>没有以管理员运行 → 释放守卫,请求 UAC 提权并重启自己,本进程退出;用户拒绝 UAC 则以普通权限继续(功能会提示"需要管理员");</item>
/// <item>开发调试可加 <c>--no-elevate</c> 或设环境变量 <c>WAIO_DEV=1</c> 跳过提权。</item>
/// <item>安装包用 <c>--register-autostart</c> / <c>--unregister-autostart</c>(ADR-0031):只建 / 删开机自启任务,不启动界面,退出码 0 = 成功。</item>
/// </list>
/// </summary>
public sealed class StartupSession : IDisposable
{
    private const string InstanceName = "WAIO.Main";
    private const string AutostartArg = "--autostart";
    private const string RelaunchedArg = "--relaunched";
    private const string RegisterAutostartArg = "--register-autostart";
    private const string UnregisterAutostartArg = "--unregister-autostart";

    private readonly SingleInstanceGuard? _guard;

    private StartupSession(SingleInstanceGuard? guard, bool showSettingsOnStart, bool startInputEngine = true)
    {
        _guard = guard;
        ShowSettingsOnStart = showSettingsOnStart;
        StartInputEngine = startInputEngine;
        if (guard is not null)
        {
            guard.ActivationRequested += () => ActivationRequested?.Invoke();
        }
    }

    /// <summary>false = 本进程应立即退出(已通知旧实例,或已发起提权实例)。</summary>
    public bool ShouldRun => _guard is not null;

    /// <summary>不启动界面时的进程退出码(安装包据此判断自启任务是否建成)。</summary>
    public int ExitCode { get; private init; }

    /// <summary>开机自启时静默驻留托盘;用户手动启动时直接显示设置窗口。</summary>
    public bool ShowSettingsOnStart { get; }

    /// <summary>
    /// false(<c>--no-hook</c>)= 不安装全局键盘钩子,只跑托盘和界面。用于冒烟测试/调试界面,避免影响正在使用的键盘。
    /// </summary>
    public bool StartInputEngine { get; }

    /// <summary>另一个实例请求本实例显示设置窗口(在后台线程触发)。</summary>
    public event Action? ActivationRequested;

    public static StartupSession Begin(string[] args)
    {
        if (args.Contains(RegisterAutostartArg) || args.Contains(UnregisterAutostartArg))
        {
            // 和设置页的开关走同一套 AutostartTask:同样只允许 Program Files 下的 exe,同样防任务 XML 被替换
            var task = new AutostartTask(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var result = args.Contains(RegisterAutostartArg) ? task.Install(Environment.ProcessPath!) : task.Uninstall();
            return new StartupSession(null, false) { ExitCode = result.Ok ? 0 : 1 };
        }

        var autostart = args.Contains(AutostartArg);
        var relaunched = args.Contains(RelaunchedArg);
        var skipElevation = relaunched
            || args.Contains("--no-elevate")
            || Environment.GetEnvironmentVariable("WAIO_DEV") == "1";

        // 提权重启的新实例要等旧实例释放守卫(旧实例发起重启后会很快退出)
        var guard = relaunched ? AcquireWithRetry() : SingleInstanceGuard.TryAcquire(InstanceName);
        if (guard is null)
        {
            NotifyExistingInstance(autostart);
            return new StartupSession(null, false);
        }

        if (!skipElevation && !WindowsCapabilityProbe.IsElevated())
        {
            guard.Dispose(); // 先释放,否则提权的新实例会发现"已有实例"
            var arguments = autostart ? $"{RelaunchedArg} {AutostartArg}" : RelaunchedArg;
            if (ElevationRelauncher.TryRelaunchElevated(Environment.ProcessPath!, arguments))
            {
                return new StartupSession(null, false);
            }

            guard = SingleInstanceGuard.TryAcquire(InstanceName); // 用户拒绝了 UAC:以普通权限继续
            if (guard is null)
            {
                NotifyExistingInstance(autostart);
                return new StartupSession(null, false);
            }
        }

        return new StartupSession(guard, showSettingsOnStart: !autostart, startInputEngine: !args.Contains("--no-hook"));
    }

    public void Dispose() => _guard?.Dispose();

    private static SingleInstanceGuard? AcquireWithRetry()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var guard = SingleInstanceGuard.TryAcquire(InstanceName);
            if (guard is not null || DateTime.UtcNow >= deadline)
            {
                return guard;
            }

            Thread.Sleep(200);
        }
    }

    private static void NotifyExistingInstance(bool autostart)
    {
        if (autostart || SingleInstanceGuard.SignalExisting(InstanceName))
        {
            return; // 开机自启遇到已有实例:静默退出,不打扰用户
        }

        // 打不开事件:常见原因是已有实例权限更高而我们是普通权限(见 SingleInstanceGuard 的"未验证"说明)
        MessageBoxW(0, "WAIO 已经在运行,请在系统托盘区找到它的图标打开设置。", "WAIO", 0x40 /* MB_ICONINFORMATION */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
