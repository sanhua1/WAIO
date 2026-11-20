using System.Diagnostics;

namespace Waio.Core.Launch;

/// <summary>
/// 静默运行下载好的安装包(自动更新,ADR-0031)。安装包会先结束正在运行的 WAIO、覆盖文件,装完以 <c>--autostart</c> 重新启动。
/// 安装包需要管理员权限:WAIO 本身是管理员,子进程直接继承,不弹 UAC。只应传入 <c>UpdateDownloader</c> 校验过的文件。
/// </summary>
public static class InstallerRunner
{
    public static void RunSilent(string installerPath)
    {
        var log = Path.ChangeExtension(installerPath, ".log");
        var startInfo = new ProcessStartInfo(installerPath, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{log}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动安装包");
    }
}
