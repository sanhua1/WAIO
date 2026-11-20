using Waio.Feature.Clipboard.Domain;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Clipboard.Panel;

/// <param name="Anchor">面板要贴着的位置(文字光标所在行、或鼠标处);null = 显示器正中。</param>
/// <param name="WorkArea">目标显示器的工作区。</param>
/// <param name="Scale">目标显示器的缩放比例(100% = 1.0)。</param>
internal sealed record PanelPlacement(ScreenRect? Anchor, ScreenRect WorkArea, double Scale);

/// <summary>面板视图(WinUI 窗口)。只负责显示和采集输入,不含业务判断;所有方法在 UI 线程调用。</summary>
internal interface IPanelView
{
    bool IsVisible { get; }

    /// <summary>
    /// 面板是当前前台窗口(用户真的看得到、按键会进来)。可能出现"显示着但没被激活"的情况
    /// (激活被别的程序抢走),这时再按快捷键应当重新激活,而不是把它当成"已打开 → 关闭"。
    /// </summary>
    bool IsActive { get; }

    /// <summary>用当前状态刷新列表和选中项。</summary>
    void Render(PickerState state);

    /// <summary>显示预览。<paramref name="content"/> 为 null 表示还在加载或内容已丢失。</summary>
    void ShowPreview(ClipItem item, ClipboardContent? content);

    void ClosePreview();

    /// <summary>程序化设置搜索框文字(例如 Esc 清空)。</summary>
    void SetQuery(string text);

    void FocusSearch();

    void FocusList();

    /// <summary>定位、显示并激活窗口。</summary>
    void Show(PanelPlacement placement, bool focusSearch);

    void Hide();
}

/// <summary>前台窗口相关:记住弹出前在用哪个窗口,粘贴前还给它。</summary>
internal interface IWindowFocus
{
    nint Foreground();

    /// <summary>把焦点还给 <paramref name="window"/> 并等它真的成为前台;超时返回 false。</summary>
    Task<bool> RestoreAsync(nint window);
}

/// <summary>算面板弹在哪(光标、鼠标或屏幕中央),依据设置。取文字光标要跨进程询问目标程序,所以是异步的。</summary>
internal interface IPlacementProvider
{
    Task<PanelPlacement> LocateAsync(nint foreground, ClipboardSettings settings);
}
