using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Waio.Feature.Translate.Domain;
using Waio.UI;
using Windows.Foundation;
using Windows.Graphics;

namespace Waio.Feature.Translate.Views;

/// <summary>
/// 显示、定位与焦点管理(MAIO <c>TranslateWindowController</c>):
/// <list type="bullet">
/// <item>打开时强制把窗口带到前台(否则输入框拿不到键盘焦点),关闭时把焦点还给原来的窗口;</item>
/// <item>窗口高度始终等于内容高度;内容变高时"屏幕正中间"模式保持中心不动,其他模式保持左上角不动;</item>
/// <item>失去焦点时关闭(设置可关;📌 固定时不关)。和剪贴板面板一样,用"定时看前台窗口"判断,不只靠激活事件。</item>
/// </list>
/// </summary>
internal sealed partial class TranslateWindow
{
    private static readonly TimeSpan DismissPollInterval = TimeSpan.FromMilliseconds(120);

    private readonly Func<TranslateSettings> _settings;
    private readonly Action<TranslateSettings> _saveSettings;
    private readonly DispatcherQueueTimer _dismissTimer;
    private nint _previousForeground;
    private bool _wasForeground;
    private (int X, int Y) _topLeft;

    /// <summary>"屏幕正中间"模式:窗口中心(随结果变高时上下同时扩展;拖动后以新位置为中心)。</summary>
    private (int X, int Y)? _center;

    private MonitorInfo _monitor = new(new ScreenRect(0, 0, 1920, 1080), 1);

    /// <summary><paramref name="text"/> 为 null 时只打开窗口(保留上次的内容);否则填入并立即翻译。</summary>
    public void Show(string? text, ShowTrigger trigger)
    {
        var current = _settings();
        var foreground = WindowChrome.Foreground();
        if (!WindowChrome.OwnsForeground(this) && foreground != 0)
        {
            _previousForeground = foreground;
        }

        _session.Prepare(text);
        if (!AppWindow.IsVisible)
        {
            Position(current, trigger);
            AppWindow.Show(activateWindow: true);
        }

        WindowChrome.ActivateForeground(this);
        SourceBox.Focus(FocusState.Programmatic);
        _wasForeground = WindowChrome.OwnsForeground(this);
        _dismissTimer.Start();
        if (text is not null)
        {
            _session.Translate();
        }
    }

    public void Dismiss()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }

        _dismissTimer.Stop();
        _session.Cancel();
        _session.IsPinned = false;
        var current = _settings();
        if (current.WindowPosition == WindowPosition.Remember)
        {
            var position = AppWindow.Position;
            current.RememberedOrigin = [position.X, position.Y];
            _saveSettings(current);
        }

        var hadFocus = WindowChrome.OwnsForeground(this);
        AppWindow.Hide();
        if (hadFocus && _previousForeground != 0)
        {
            _ = WindowChrome.RestoreForegroundAsync(_previousForeground, TimeSpan.FromMilliseconds(500));
        }

        _previousForeground = 0;
    }

    private void Position(TranslateSettings current, ShowTrigger trigger)
    {
        var mouse = ScreenLocator.CursorPosition();
        var anchor = trigger.SelectionRect is { } rect && WindowPlacement.Kind(trigger, current) == PlacementKind.Below
            ? new ScreenPoint(rect.CenterX, rect.CenterY)
            : mouse;
        _monitor = ScreenLocator.MonitorAt(anchor);
        var work = _monitor.WorkArea;
        var box = new Box(work.Left, work.Top, work.Right, work.Bottom);
        var (width, height) = MeasureContent(current.WindowWidth);
        var kind = WindowPlacement.Kind(trigger, current);
        var reserve = (int)(WindowPlacement.ReserveDip * _monitor.Scale);
        var margin = (int)(WindowPlacement.MarginDip * _monitor.Scale);
        _topLeft = WindowPlacement.TopLeft(kind, box, width, height, reserve, margin, (mouse.X, mouse.Y), trigger.SelectionRect, current.RememberedOrigin);
        _center = kind == PlacementKind.Center ? (box.CenterX, box.CenterY) : null;
        AppWindow.MoveAndResize(new RectInt32(_topLeft.X, _topLeft.Y, width, height));
    }

    /// <summary>按内容重新计算高度,并按规则保持中心或左上角不动(MAIO didResize)。</summary>
    private void FitToContent()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }

        var (width, height) = MeasureContent(_settings().WindowWidth);
        var size = AppWindow.Size;
        if (size.Width == width && size.Height == height)
        {
            return;
        }

        var work = _monitor.WorkArea;
        var position = AppWindow.Position;
        if (_center is { } center && (position.X != center.X - size.Width / 2 || Math.Abs(position.Y - (center.Y - size.Height / 2)) > 1))
        {
            // 用户拖动过:以新位置为中心(MAIO didMove)
            _center = (position.X + size.Width / 2, position.Y + size.Height / 2);
        }

        var (x, y) = WindowPlacement.AfterResize((position.X, position.Y), _center, width, height, new Box(work.Left, work.Top, work.Right, work.Bottom));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>内容的大小(物理像素)。结果区最高为屏幕的 60%(MAIO maxResultsHeight),超出部分滚动。</summary>
    private (int Width, int Height) MeasureContent(double widthDip)
    {
        var scale = _monitor.Scale;
        ResultsScroller.MaxHeight = Math.Max(240, _monitor.WorkArea.Height / scale * 0.6);
        Root.Measure(new Size(widthDip, double.PositiveInfinity));
        var heightDip = Math.Min(Root.DesiredSize.Height, _monitor.WorkArea.Height / scale);
        return ((int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(Math.Max(heightDip, 120) * scale));
    }

    private DispatcherQueueTimer CreateDismissTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Interval = DismissPollInterval;
        timer.Tick += (_, _) => CheckDismiss();
        return timer;
    }

    /// <summary>MAIO didResignKey:设置要求失去焦点关闭、且没有固定时,用户点到别处就关闭。</summary>
    private void CheckDismiss()
    {
        if (!AppWindow.IsVisible)
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
        if (foreground == 0 || !_wasForeground && foreground == _previousForeground || WindowChrome.IsOwnWindow(foreground))
        {
            return; // 前台切换的瞬间;或窗口还没真正拿到前台;或前台是 WAIO 的另一个窗口(共存)
        }

        if (_settings().CloseOnBlur && !_session.IsPinned)
        {
            _previousForeground = 0; // 用户自己点了别的窗口:不要再把焦点抢回原窗口
            Dismiss();
        }
    }
}
