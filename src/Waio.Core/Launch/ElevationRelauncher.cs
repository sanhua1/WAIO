using System.ComponentModel;
using System.Diagnostics;

namespace Waio.Core.Launch;

/// <summary>单实例 + 提权重启(ADR-0004 §4):用户以普通权限启动时,请求 UAC 提权后重新启动自己。</summary>
public static class ElevationRelauncher
{
    private const int ErrorCancelled = 1223; // 用户在 UAC 弹窗里点了"否"

    /// <returns>true = 已发起提权实例;false = 用户拒绝了 UAC。其他失败抛异常。</returns>
    public static bool TryRelaunchElevated(string exePath, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas", Arguments = arguments ?? "" })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}
