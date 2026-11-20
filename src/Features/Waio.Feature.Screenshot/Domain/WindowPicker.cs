namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 冻结时读取的一个顶层窗口(MAIO <c>WindowInfo</c>)。顺序与 Z 序一致:越靠前越在上层。
/// </summary>
/// <param name="Frame">窗口的可见边界(DWM 扩展边框,不含阴影),全局物理像素。</param>
/// <param name="Alpha">分层窗口的整体透明度(0~1);普通窗口为 1。</param>
/// <param name="ClickThrough">鼠标穿透(WS_EX_TRANSPARENT):全屏的透明浮层(游戏/显卡覆盖层等)常这样。</param>
/// <param name="ExcludedFromCapture">
/// 窗口设了"不出现在截屏里"(<c>SetWindowDisplayAffinity</c> 非 0,如录屏控制条、长截图边框、一些密码管理器):
/// 冻结的画面里看不到它(或者是黑块),不能当作可选窗口。
/// </param>
public sealed record WindowInfo(
    nint Handle, int ProcessId, PxRect Frame, string ClassName, double Alpha = 1, bool ClickThrough = false,
    bool ExcludedFromCapture = false);

/// <summary>从冻结的窗口列表里挑出鼠标下的窗口(MAIO <c>WindowPicker</c>)。</summary>
public static class WindowPicker
{
    /// <summary>太小的窗口(例如 1×1 的辅助窗口)不参与识别。</summary>
    public const int MinimumSize = 8;

    /// <summary>
    /// 桌面背景窗口:覆盖整个屏幕,不排除的话处处都会命中它(对应 MAIO 排除程序坞那个覆盖全屏的透明窗口)。
    /// 鼠标下没有窗口时,调用方高亮整个屏幕(MAIO)。
    /// </summary>
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW" };

    /// <summary>
    /// 过滤掉不应该被选中的窗口,保持原有的上下顺序。
    /// 原则:冻结画面里看得见的窗口都能选,WAIO 自己的窗口(设置、启动台、剪贴板面板、贴图)也一样;
    /// 截图遮罩本身在读窗口列表时还没显示,不会混进来。
    /// </summary>
    public static IReadOnlyList<WindowInfo> Candidates(IEnumerable<WindowInfo> windows) =>
        [.. windows.Where(w => !w.ExcludedFromCapture
            && w.Alpha >= 0.05
            && !w.ClickThrough
            && w.Frame.Width >= MinimumSize && w.Frame.Height >= MinimumSize
            && !DesktopClasses.Contains(w.ClassName))];

    public static WindowInfo? WindowAt(PxPoint point, IReadOnlyList<WindowInfo> candidates) =>
        candidates.FirstOrDefault(w => w.Frame.Contains(point));
}
