using Microsoft.Win32;

namespace Waio.Core.Launch;

/// <summary>
/// 系统的 UAC(用户账户控制)是否开启。UAC 关闭时所有进程默认就是管理员令牌,
/// "降权启动"(ADR-0006)没有"普通令牌"可用,ADR-0004 的提权保护措施也就形同虚设——需要明确告知用户。
/// 只读系统策略,不写注册表。
/// </summary>
public static class UacStatus
{
    /// <summary>读不到该策略值时按"已开启"处理(Windows 默认开启)。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            return key?.GetValue("EnableLUA") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
