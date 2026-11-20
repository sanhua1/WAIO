using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Windows.System;
using Windows.UI.Core;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 截图时的鼠标和键盘(MAIO CaptureSession+Input):先交给标注编辑,标注不处理的再交给选区。按 Windows 习惯,MAIO 的 ⌘ 对应 Ctrl。
/// <list type="bullet">
/// <item>拖动框选;选区内拖动是移动,拖手柄是调整大小;单击选中高亮的窗口 / 控件;双击选区、⏎ 复制并结束;</item>
/// <item>Ctrl+C 复制(没有选区时复制颜色值);C 复制颜色值;单独按一下 Shift 切换 HEX / RGB;Ctrl+A 选中整个屏幕;</item>
/// <item>Ctrl+S 保存;Ctrl+P 贴图;方向键 / Shift+方向键 移动 / 调整大小 1 像素(选中标注时移动标注);按住 Ctrl 识别控件;</item>
/// <item>R O A L P H T M B N 选工具;Ctrl+Z / Ctrl+Shift+Z(Ctrl+Y)撤销重做;Delete / Backspace 删除选中的标注;</item>
/// <item>Esc、右键逐层退出:取消选中标注 → 取消工具 → 取消选区 → 退出;已经有标注时不再取消选区,直接退出。</item>
/// </list>
/// </summary>
internal sealed partial class CaptureSession
{
    private bool _pressed;
    private bool _ctrlHeld;
    private bool _shiftAlone;
    private long _lastDownTicks;
    private PxPoint _lastDownPoint;

    public void OnPointerPressed(OverlayWindow source, bool right)
    {
        _cursor = CursorNow();
        if (right)
        {
            CancelOneLevel(); // MAIO overlayRightMouseDown
            return;
        }

        // 正在输入文字时,点别处只结束输入
        if (_textField.IsEditing)
        {
            CommitText();
            Render();
            return;
        }

        var display = DisplayAt(_cursor);
        if (display is null)
        {
            return;
        }

        _pressed = true;
        var clicks = ClickCount(display.Display.Scale);
        if (AnnotationMouseDown(clicks, display.Display.Bounds))
        {
            SyncToolbar();
            UpdateCursorShape();
            Render();
            return;
        }

        // 重新编辑时选区固定:只有双击图片(完成)有效(MAIO)
        if (_edit is { } edit)
        {
            _pressed = false;
            if (clicks >= 2 && edit.Rect.Contains(_cursor))
            {
                Confirm(_options.PrimaryAction);
            }

            return;
        }

        // 已经有标注时不能重新框选(标注会落到选区外面)
        if (!_editor.Document.IsEmpty && _interaction.Selection is { } selection && !selection.Contains(_cursor)
            && SelectionGeometry.Handle(_cursor, selection, _interaction.HandleTolerance) is null)
        {
            _pressed = false;
            return;
        }

        var effect = _interaction.MouseDown(_cursor, clicks, display.Display.Bounds, display.Display.Scale);
        if (effect == CaptureInteraction.Effect.Confirm)
        {
            Confirm(_options.PrimaryAction);
            return;
        }

        UpdateCursorShape();
        Render();
    }

    /// <summary>返回 true 表示标注处理了这次点击(MAIO annotationMouseDown)。</summary>
    private bool AnnotationMouseDown(int clicks, PxRect displayBounds)
    {
        if (_interaction.Selection is not { } selection || displayBounds != _interaction.Bounds)
        {
            return false;
        }

        _editor.Scale = EditorScale;
        var point = new PointD(_cursor.X, _cursor.Y);
        // 选区的手柄优先(除非点在选中标注自己的手柄上)
        if (_edit is null && _editor.Document.Selected?.HandleAt(point, _editor.HandleTolerance) is null
            && SelectionGeometry.Handle(_cursor, selection, _interaction.HandleTolerance) is not null)
        {
            return false;
        }

        switch (_editor.MouseDown(point, clicks, RectD.From(selection)))
        {
            case AnnotationEditor.Outcome.BeginText begin:
                _pressed = false;
                BeginText(_editor.NewTextBox(begin.Point), _editor.Style.ColorFor(AnnotationTool.Text), null);
                return true;
            case AnnotationEditor.Outcome.EditText edit:
                _pressed = false;
                EditText(edit.Id);
                return true;
            case AnnotationEditor.Outcome.Handled:
                return true;
            default:
                return false;
        }
    }

    public void OnPointerMoved(OverlayWindow source, bool leftPressed)
    {
        _cursor = CursorNow();
        if (_editor.IsDragging && leftPressed)
        {
            _editor.MouseDragged(new PointD(_cursor.X, _cursor.Y), IsDown(VirtualKey.Shift));
        }
        else if (_pressed && leftPressed)
        {
            _interaction.MouseDragged(_cursor);
            if (_interaction.Selection is not null)
            {
                _highlight = null;
            }
        }
        else
        {
            UpdateHover();
        }

        UpdateColor();
        UpdateCursorShape();
        Render();
    }

    public void OnPointerReleased(OverlayWindow source)
    {
        if (!_pressed)
        {
            return; // 释放捕获时还会再来一次
        }

        _pressed = false;
        _cursor = CursorNow();
        if (_editor.IsDragging)
        {
            _editor.MouseUp();
            SyncToolbar();
        }
        else
        {
            _interaction.MouseUp(_highlight);
            if (_interaction.Selection is not null)
            {
                _highlight = null;
            }
        }

        UpdateCursorShape();
        Render();
    }

    public bool OnKeyDown(VirtualKey key)
    {
        if (_textField.IsEditing)
        {
            return false; // 正在输入文字时,按键都交给输入框
        }

        if (key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift)
        {
            // 单独按一下 Shift(中间没有按别的键)切换颜色格式;正在画图时 Shift 是吸附,不切换(MAIO overlayFlagsChanged)
            _shiftAlone = !_interaction.IsDragging && !_editor.IsDragging && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu);
            return true;
        }

        _shiftAlone = false;
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            SetCtrlHeld(true);
            return true;
        }

        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (IsDown(VirtualKey.Menu) && key != VirtualKey.Escape)
        {
            return false;
        }

        if (HandleAnnotationKey(key, ctrl, shift))
        {
            SyncToolbar();
            UpdateCursorShape();
            Render();
            return true;
        }

        switch (key)
        {
            case VirtualKey.Escape:
                CancelOneLevel();
                break;
            case VirtualKey.Enter when !ctrl && !shift:
                Confirm(_options.PrimaryAction);
                break;
            case VirtualKey.C when ctrl && !shift:
                if (_interaction.Selection is not null)
                {
                    Confirm(CaptureAction.Copy);
                }
                else
                {
                    CopyColor();
                }

                break;
            case VirtualKey.C when !ctrl && !shift:
                CopyColor();
                break;
            case VirtualKey.A when ctrl && !shift:
                SelectWholeDisplay();
                break;
            case VirtualKey.S when ctrl && !shift:
                Confirm(CaptureAction.Save);
                break;
            case VirtualKey.P when ctrl && !shift:
                Confirm(CaptureAction.Pin);
                break;
            case VirtualKey.O when ctrl && !shift:
                Confirm(CaptureAction.Ocr);
                break;
            case VirtualKey.T when ctrl && !shift:
                Confirm(CaptureAction.Translate);
                break;
            case VirtualKey.L when ctrl && !shift:
                Confirm(CaptureAction.Scrolling);
                break;
            case VirtualKey.R when ctrl && !shift:
                Confirm(CaptureAction.Record);
                break;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when !ctrl:
                Nudge(key, resize: shift);
                break;
            default:
                return key == VirtualKey.Tab; // Tab 不要移动焦点
        }

        return true;
    }

    public void OnKeyUp(VirtualKey key)
    {
        if (key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift)
        {
            if (_shiftAlone)
            {
                _options.ColorFormat = _options.ColorFormat.Next();
                _callbacks.ColorFormatChanged(_options.ColorFormat);
                Render();
            }

            _shiftAlone = false;
        }
        else if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            SetCtrlHeld(false);
        }
    }

    /// <summary>
    /// Esc / 右键逐层退出(MAIO cancelOneLevel):取消选中对象 → 取消工具 → 取消选区 → 退出截图。
    /// 已经有标注时,取消选区会丢掉标注,所以这一层改为直接退出。
    /// </summary>
    private void CancelOneLevel()
    {
        _pressed = false;
        if (_textField.IsEditing)
        {
            CommitText();
            Render();
            return;
        }

        if (_editor.Escape())
        {
            SyncToolbar();
            UpdateCursorShape();
            Render();
            return;
        }

        // 重新编辑时没有"取消选区"这一层,直接退出(MAIO)
        if (_edit is not null || !_editor.Document.IsEmpty || _interaction.Cancel() == CaptureInteraction.Effect.Exit)
        {
            Finish(new CaptureOutcome.Cancelled());
            return;
        }

        UpdateHover();
        UpdateCursorShape();
        Render();
    }

    private void SelectWholeDisplay()
    {
        if (_edit is null && _editor.Document.IsEmpty && DisplayAt(_cursor) is { } display)
        {
            _interaction.Select(display.Display.Bounds, display.Display.Bounds, display.Display.Scale);
            _highlight = null;
            SyncToolbar();
            UpdateCursorShape();
            Render();
        }
    }

    /// <summary>方向键:移动或调整大小,每次 1 个物理像素(MAIO pixelStep)。</summary>
    private void Nudge(VirtualKey key, bool resize)
    {
        if (_edit is not null)
        {
            return;
        }

        var (dx, dy) = Direction(key);
        _interaction.Nudge(dx, dy, resize);
        Render();
    }

    private static (int Dx, int Dy) Direction(VirtualKey key) => key switch
    {
        VirtualKey.Left => (-1, 0),
        VirtualKey.Right => (1, 0),
        VirtualKey.Down => (0, 1),
        _ => (0, -1),
    };

    private void SetCtrlHeld(bool held)
    {
        if (held == _ctrlHeld)
        {
            return;
        }

        _ctrlHeld = held;
        UpdateHover();
        Render();
    }

    /// <summary>自己数点击次数:按系统的双击时间和双击范围判断(MAIO 用 NSEvent.clickCount)。</summary>
    private int ClickCount(double scale)
    {
        var now = Environment.TickCount64;
        var near = Math.Abs(_cursor.X - _lastDownPoint.X) <= 4 * scale && Math.Abs(_cursor.Y - _lastDownPoint.Y) <= 4 * scale;
        var count = near && now - _lastDownTicks <= GetDoubleClickTime() ? 2 : 1;
        _lastDownTicks = count == 2 ? 0 : now;
        _lastDownPoint = _cursor;
        return count;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
