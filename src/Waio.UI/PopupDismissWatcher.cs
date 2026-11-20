using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Waio.UI;

/// <summary>
/// 弹出面板"点到别处就关闭"(和任何弹出菜单一样)。做法来自剪贴板面板(ClipboardPanelWindow.Dismiss.cs),抽出来给启动台等复用。
/// <para>
/// 只靠窗口的"失去激活"事件不可靠:面板如果一开始就没抢到前台(前台锁),它从未被激活,也就永远等不到"失去激活"。
/// 所以面板显示期间再定时看一眼前台窗口:前台曾经是面板、现在变成了别的窗口 → 关闭;
/// 面板从没成为前台、而前台从弹出前的窗口换成了别的 → 也说明用户点到别处了,关闭。
/// 面板自己弹出的右键菜单(以面板为所有者的窗口)算面板的一部分。
/// </para>
/// 在 UI 线程创建和使用。
/// </summary>
public sealed class PopupDismissWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    private readonly Window _window;
    private readonly Action _dismiss;
    private readonly DispatcherQueueTimer _timer;
    private nint _foregroundBeforeShow;
    private bool _wasForeground;

    /// <param name="dismiss">判断用户点到了别处时调用(UI 线程)。</param>
    public PopupDismissWatcher(Window window, Action dismiss)
    {
        _window = window;
        _dismiss = dismiss;
        _timer = window.DispatcherQueue.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = PollInterval;
        _timer.Tick += (_, _) => Check();
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                Check(); // 立刻检查一次(右键菜单弹出时也会触发"失去激活",所以不直接关闭)
            }
        };
    }

    /// <summary>面板刚显示时调用。<paramref name="foregroundBeforeShow"/>:弹出前的前台窗口。</summary>
    public void Start(nint foregroundBeforeShow)
    {
        _foregroundBeforeShow = foregroundBeforeShow;
        _wasForeground = WindowChrome.OwnsForeground(_window);
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Check()
    {
        if (!_timer.IsRunning || !_window.AppWindow.IsVisible)
        {
            _timer.Stop();
            return;
        }

        if (WindowChrome.OwnsForeground(_window))
        {
            _wasForeground = true;
            return;
        }

        var foreground = WindowChrome.Foreground();
        if (foreground == 0 || WindowChrome.IsOwnWindow(foreground))
        {
            // 前台切换的瞬间可能没有前台窗口,下一轮再看;
            // 前台是 WAIO 的另一个窗口(剪贴板、翻译、截图……)时面板共存,不关
            return;
        }

        if (_wasForeground || foreground != _foregroundBeforeShow)
        {
            _timer.Stop();
            _dismiss();
        }
    }
}
