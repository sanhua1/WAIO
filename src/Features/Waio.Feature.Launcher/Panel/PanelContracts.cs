using Waio.Feature.Launcher.Domain;
using Waio.UI;

namespace Waio.Feature.Launcher.Panel;

/// <param name="WorkArea">鼠标所在显示器的工作区(物理像素)。</param>
/// <param name="Scale">该显示器的缩放比例(100% = 1.0)。</param>
/// <param name="WidthPercent">面板宽占工作区的百分比(设置项,换算规则见 <see cref="PanelGeometry"/>)。</param>
/// <param name="HeightPercent">面板高占工作区的百分比。</param>
internal sealed record PanelPlacement(
    ScreenRect WorkArea, double Scale,
    int WidthPercent = PanelGeometry.DefaultWidthPercent, int HeightPercent = PanelGeometry.DefaultHeightPercent);

/// <summary>面板视图(WinUI 窗口)。只负责显示和采集输入,不做判断;所有方法在 UI 线程调用。</summary>
internal interface ILauncherView
{
    bool IsVisible { get; }

    /// <summary>面板是当前前台窗口(显示着但被别的程序抢了前台时为 false,再按快捷键应当重新激活而不是关闭)。</summary>
    bool IsActive { get; }

    /// <summary>用当前状态刷新标签、列表和选中项。</summary>
    void Render(LauncherState state);

    /// <summary>程序化设置搜索框文字(Esc 清空、Tab 去掉前缀),光标放到末尾。</summary>
    void SetQuery(string text);

    /// <summary>定位、显示、激活,焦点放进搜索框。</summary>
    void Show(PanelPlacement placement);

    void Hide();
}

/// <summary>系统相关的小动作:前台窗口、面板位置。</summary>
internal interface IPanelSystem
{
    nint Foreground();

    /// <summary>把焦点还给 <paramref name="window"/>(Esc 关闭时回到原来的程序)。</summary>
    Task RestoreAsync(nint window);

    /// <summary>
    /// 允许接下来启动的程序把自己的窗口带到前台。必须在面板还是前台时调用(Windows 只允许前台进程放行)。
    /// </summary>
    void AllowForegroundHandoff();

    PanelPlacement Locate();
}

/// <summary>文件搜索(全盘索引)。在后台线程调用;没开文件搜索时为 null。</summary>
internal interface IFileSearch
{
    /// <summary>按输入搜文件;<paramref name="foldersOnly"/> 只要文件夹。</summary>
    FileResults Search(string text, bool foldersOnly, int limit, CancellationToken cancellation);
}
