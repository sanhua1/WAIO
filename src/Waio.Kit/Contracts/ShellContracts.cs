using Waio.Kit.Commands;

namespace Waio.Kit.Contracts;

/// <summary>
/// 宿主(Shell)提供给功能的契约(ADR-0025)。提供方:Shell;调用方:启动台底栏的齿轮按钮。
/// </summary>
public static class ShellContracts
{
    /// <summary>
    /// 打开 WAIO 设置窗口(已打开就置前,和左键点托盘图标一样)。可在任意线程调用,立即返回。
    /// </summary>
    public static readonly CommandContract<Unit, Unit> OpenSettings = new("shell.openSettings");
}
