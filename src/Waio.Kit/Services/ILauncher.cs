namespace Waio.Kit.Services;

/// <summary>
/// 启动请求。<see cref="Target"/> 可以是可执行文件(完整路径,或 PATH 里的名字如 "notepad")、
/// 文档/文件夹/快捷方式路径,或 http/https/mailto/ms-settings 链接。支持 %环境变量%。
/// </summary>
/// <param name="Arguments">命令行参数(原样传给可执行文件)。仅可执行文件目标支持。</param>
/// <param name="RunElevated">
/// 保持当前(管理员)权限启动。默认 false:以普通用户权限启动(ADR-0004、ADR-0006)。
/// 只应来自用户在绑定上的显式勾选。
/// </param>
public sealed record LaunchRequest(
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null,
    bool RunElevated = false);

/// <summary>
/// 启动外部程序/文件/链接。WAIO 常驻管理员,所以默认降权启动,
/// 否则任何能改配置文件的普通进程都能借它获得管理员执行权(ADR-0004)。
/// </summary>
public interface ILauncher
{
    /// <exception cref="LaunchException">目标无效、找不到,或系统拒绝启动。<c>OperationCanceledException</c> 除外。</exception>
    ValueTask LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>启动失败。消息面向用户(中文),可直接显示在 HUD 上。</summary>
public sealed class LaunchException(string message, Exception? inner = null) : Exception(message, inner);
