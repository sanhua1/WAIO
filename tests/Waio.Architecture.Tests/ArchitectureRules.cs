namespace Waio.Architecture.Tests;

/// <summary>
/// 架构规则的单一事实来源。对应 docs/ARCHITECTURE.md §2 的依赖表。
/// 修改这里 = 修改架构,必须同时发 ADR。
/// </summary>
internal static class ArchitectureRules
{
    public const string Kit = "Waio.Kit";
    public const string Core = "Waio.Core";
    public const string UI = "Waio.UI";
    public const string Shell = "Waio.Shell";
    public const string Testing = "Waio.Testing";
    public const string App = "Waio.App";
    public const string FeaturePrefix = "Waio.Feature.";

    /// <summary>Kit 唯一允许的 NuGet 依赖。</summary>
    public static readonly IReadOnlyList<string> KitPackageAllowlist = ["Microsoft.Extensions.Logging.Abstractions"];

    /// <summary>返回某个项目允许引用的 WAIO 项目判定函数;项目名未登记时返回 null。</summary>
    public static Func<string, bool>? AllowedReferences(string project)
    {
        if (project.StartsWith(FeaturePrefix, StringComparison.Ordinal))
        {
            return r => r is Kit or UI; // 功能只能看到 SDK 和共享 UI,看不到 Core、Shell、其他功能
        }

        return project switch
        {
            Kit => _ => false,
            Core => r => r is Kit,
            UI => r => r is Kit,
            Testing => r => r is Kit,
            Shell => r => r is Kit or UI or Core,
            // App 是组装根:还要把各功能的设置页(Waio.UI 类型)交给 Shell(ADR-0010)
            App => r => r is Kit or UI or Shell || r.StartsWith(FeaturePrefix, StringComparison.Ordinal),
            _ => null,
        };
    }

    /// <summary>只有 Core 的输入引擎可以出现的 API(全局钩子、原始输入、合成按键)。</summary>
    public static readonly IReadOnlyList<string> InputEngineOnlyTokens =
        ["SetWindowsHookEx", "RegisterRawInputDevices", "SendInput(", "GetAsyncKeyState", "GetKeyboardState"];

    /// <summary>
    /// 只有 Waio.Core/Launch 可以出现的 API(创建进程)。常驻管理员下,子进程会继承管理员令牌,
    /// 所以必须统一走 <c>ILauncher</c> 并默认降权(ADR-0006)。自我提权重启、计划任务自启也放在 Launch 目录里。
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessCreationOnlyTokens =
        ["Process.Start", "ProcessStartInfo", "ShellExecute", "CreateProcess"];

    /// <summary>功能代码里禁止的其他 API:必须通过 context 提供的服务。</summary>
    public static readonly IReadOnlyList<string> FeatureForbiddenTokens =
        ["Environment.GetFolderPath", "AppContext.BaseDirectory"];

    /// <summary>Realtime/ 目录(在钩子线程同步运行)里禁止的标记:UI 线程 API、异步、阻塞。</summary>
    public static readonly IReadOnlyList<string> RealtimeForbiddenTokens =
        ["DispatcherQueue", "Dispatcher.", "SynchronizationContext", "Application.Current",
         "async ", "await ", "Thread.Sleep", ".Wait(", ".Result", "Task.Run"];

    public const int MaxFileLines = 400;
}
