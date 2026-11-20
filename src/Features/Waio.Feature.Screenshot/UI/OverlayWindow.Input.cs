using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Waio.Feature.Screenshot.UI;

/// <summary>遮罩把鼠标键盘事件交给当前的截图会话(MAIO OverlayEventHandler)。位置统一由会话读屏幕坐标,这里只转交事件。</summary>
internal interface ICaptureInput
{
    void OnPointerPressed(OverlayWindow source, bool right);

    void OnPointerMoved(OverlayWindow source, bool leftPressed);

    void OnPointerReleased(OverlayWindow source);

    /// <returns>是否处理了这个键(处理了就不再交给 XAML,例如 Tab 不会移动焦点)。</returns>
    bool OnKeyDown(VirtualKey key);

    void OnKeyUp(VirtualKey key);

    /// <summary>点了工具栏上的按钮。</summary>
    void OnToolbar(ToolbarCommand command);

    /// <summary>滚轮(一格 = 120,向上为正)。</summary>
    void OnWheel(int delta);
}

internal sealed partial class OverlayWindow
{
    /// <summary>当前的截图会话;空闲时为 null,事件被忽略。</summary>
    public ICaptureInput? Handler { get; set; }

    private void WireInput()
    {
        _surface.PointerPressed += (_, e) =>
        {
            var properties = e.GetCurrentPoint(_surface).Properties;
            if (properties.IsRightButtonPressed)
            {
                Handler?.OnPointerPressed(this, right: true);
            }
            else if (properties.IsLeftButtonPressed)
            {
                _surface.CapturePointer(e.Pointer); // 拖出本显示器时也继续收到移动事件
                Handler?.OnPointerPressed(this, right: false);
            }

            e.Handled = true;
        };
        _surface.PointerMoved += (_, e) =>
        {
            Handler?.OnPointerMoved(this, e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed);
            e.Handled = true;
        };
        _surface.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(_surface).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
            {
                _surface.ReleasePointerCapture(e.Pointer);
                Handler?.OnPointerReleased(this);
            }

            e.Handled = true;
        };
        _surface.PointerCaptureLost += (_, _) => Handler?.OnPointerReleased(this);
        _surface.PointerWheelChanged += (_, e) =>
        {
            Handler?.OnWheel(e.GetCurrentPoint(_surface).Properties.MouseWheelDelta);
            e.Handled = true;
        };
        _surface.KeyDown += OnKeyDown;
        _surface.KeyUp += (_, e) => Handler?.OnKeyUp(e.Key);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Handler?.OnKeyDown(e.Key) == true)
        {
            e.Handled = true;
        }
    }

    /// <summary>让这个遮罩接收键盘(MAIO makeKey + makeFirstResponder)。</summary>
    public void FocusInput() => _surface.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
}
