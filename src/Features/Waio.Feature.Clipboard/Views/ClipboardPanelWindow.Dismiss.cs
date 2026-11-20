using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Waio.UI;

namespace Waio.Feature.Clipboard.Views;

/// <summary>
/// 点到别处就关闭面板,和任何弹出菜单一样。
/// <para>
/// 只靠窗口的"失去激活"事件不可靠:面板如果一开始就没抢到前台(前台锁),它从未被激活,也就永远等不到"失去激活"。
/// 所以面板显示期间再定时看一眼前台窗口:
/// 前台曾经是面板、现在变成了别的窗口 → 关闭;面板从没成为前台、而前台从弹出前的窗口换成了别的 → 也说明用户点到别处了,关闭。
/// 面板自己弹出的右键菜单(以面板为所有者的窗口)算面板的一部分。
/// </para>
/// </summary>
internal sealed partial class ClipboardPanelWindow
{
    private static readonly TimeSpan DismissPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherQueueTimer _dismissTimer;
    private nint _foregroundBeforeShow;
    private bool _wasForeground;

    private DispatcherQueueTimer CreateDismissTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Interval = DismissPollInterval;
        timer.Tick += (_, _) => CheckDismiss();
        return timer;
    }

    private void StartDismissWatch(nint foregroundBeforeShow)
    {
        _foregroundBeforeShow = foregroundBeforeShow;
        _wasForeground = WindowChrome.OwnsForeground(this);
        _dismissTimer.Start();
    }

    private void StopDismissWatch() => _dismissTimer.Stop();

    private void CheckDismiss()
    {
        if (!IsVisible)
        {
            _dismissTimer.Stop();
            return;
        }

        if (WindowChrome.OwnsForeground(this))
        {
            _wasForeground = true;
            return;
        }

        var foreground = WindowChrome.Foreground();
        if (foreground == 0 || WindowChrome.IsOwnWindow(foreground))
        {
            // 前台切换的瞬间可能没有前台窗口,下一轮再看;
            // 前台是 WAIO 的另一个窗口(启动台、翻译、截图……)时共存,不关
            return;
        }

        if (_wasForeground || foreground != _foregroundBeforeShow)
        {
            _logger.LogInformation("剪贴板面板:前台已切到别的窗口,关闭");
            _presenter?.Hide();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            CheckDismiss(); // 立刻检查一次(右键菜单弹出时也会触发"失去激活",所以不直接关闭)
        }
    }
}
