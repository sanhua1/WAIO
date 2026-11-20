using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Waio.UI;

/// <summary>
/// 无标题栏窗口的"按住某个区域拖动窗口"。
/// <para>
/// 不用"发 WM_NCLBUTTONDOWN + HTCAPTION 让系统拖"的做法:WinUI 3 窗口里那样做,系统的移动循环和 WinUI 自己的输入处理抢鼠标,
/// 2026-11-10 用户报告翻译窗口拖动时卡顿、瞬移、完全不可用。这里自己捕获指针,每次移动按<b>屏幕上的</b>鼠标位置算窗口位置
/// (起点窗口位置 + 鼠标相对起点的位移)。不能用相对窗口的指针坐标:窗口一动,相对坐标跟着变,会来回抖。
/// </para>
/// </summary>
public static class WindowDrag
{
    /// <summary>按住 <paramref name="handle"/>(左键)拖动 <paramref name="window"/>。区域里的按钮等控件自己处理按下,不会触发拖动。</summary>
    public static void Attach(Window window, UIElement handle)
    {
        var dragging = false;
        var startWindow = default(PointInt32);
        var startCursor = default(ScreenPoint);

        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || !handle.CapturePointer(e.Pointer))
            {
                return;
            }

            dragging = true;
            startWindow = window.AppWindow.Position;
            startCursor = ScreenLocator.CursorPosition();
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            var cursor = ScreenLocator.CursorPosition();
            var target = new PointInt32(startWindow.X + cursor.X - startCursor.X, startWindow.Y + cursor.Y - startCursor.Y);
            var current = window.AppWindow.Position;
            if (target.X != current.X || target.Y != current.Y)
            {
                window.AppWindow.Move(target);
            }

            e.Handled = true;
        };

        void End(object sender, PointerRoutedEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                handle.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        handle.PointerReleased += End;
        handle.PointerCanceled += End;
        handle.PointerCaptureLost += (_, _) => dragging = false;
    }
}
