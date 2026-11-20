namespace Waio.Feature.Screenshot.Domain;

/// <summary>
/// 截图时的鼠标交互状态机(MAIO <c>CaptureInteraction</c>,纯逻辑,可单测):框选、移动、调整大小、单击选中高亮的窗口。
/// 坐标是全局物理像素;"多少 pt"的阈值按按下处显示器的缩放比例换算。
/// </summary>
public sealed class CaptureInteraction
{
    /// <summary>拖动超过这个距离(pt)才算框选,否则算单击。</summary>
    public const double DragThresholdDip = 3;

    private Drag? _drag;
    /// <summary>是否已经拖出了选区(用于区分单击和框选)。</summary>
    private bool _didDragOut;
    /// <summary>新建选区时按下处的显示器范围(真正拖出选区后才替换 Bounds)。</summary>
    private PxRect _pendingBounds;
    private double _scale = 1;
    private double _pendingScale = 1;

    public enum Effect
    {
        None,
        /// <summary>复制并结束(或者 ⏎ 的其他主动作)。</summary>
        Confirm,
        /// <summary>退出截图。</summary>
        Exit,
    }

    public abstract record Drag;

    /// <summary>新建选区。<paramref name="Previous"/> 是按下前的选区:没有拖出足够距离就松手时恢复它。</summary>
    public sealed record Creating(PxPoint Start, PxRect? Previous) : Drag;

    public sealed record Moving(PxRect Original, PxPoint Start) : Drag;

    public sealed record Resizing(SelectionHandle Handle, PxRect Original) : Drag;

    /// <summary>当前选区。</summary>
    public PxRect? Selection { get; private set; }

    /// <summary>选区所在显示器的范围(选区不能跨显示器)。</summary>
    public PxRect Bounds { get; private set; }

    public Drag? CurrentDrag => _drag;

    public bool IsDragging => _drag is not null;

    public bool IsCreating => _drag is Creating;

    /// <summary>手柄的点击容差(物理像素)。</summary>
    public int HandleTolerance => (int)Math.Round(SelectionGeometry.HandleToleranceDip * _scale, MidpointRounding.AwayFromZero);

    public Effect MouseDown(PxPoint point, int clickCount, PxRect displayBounds, double scale)
    {
        if (Selection is { } selection)
        {
            if (clickCount >= 2 && selection.Contains(point))
            {
                return Effect.Confirm;
            }

            if (SelectionGeometry.Handle(point, selection, HandleTolerance) is { } handle)
            {
                _drag = new Resizing(handle, selection);
                return Effect.None;
            }

            if (selection.Contains(point))
            {
                _drag = new Moving(selection, point);
                return Effect.None;
            }
        }

        _drag = new Creating(point, Selection);
        _didDragOut = false;
        if (Selection is null)
        {
            Bounds = displayBounds;
            _scale = scale;
        }

        _pendingBounds = displayBounds;
        _pendingScale = scale;
        return Effect.None;
    }

    public void MouseDragged(PxPoint point)
    {
        switch (_drag)
        {
            case Creating creating:
                var distance = Math.Sqrt(Math.Pow(point.X - creating.Start.X, 2) + Math.Pow(point.Y - creating.Start.Y, 2));
                if (!_didDragOut && distance < DragThresholdDip * _pendingScale)
                {
                    return;
                }

                _didDragOut = true;
                Bounds = _pendingBounds;
                _scale = _pendingScale;
                var end = SelectionGeometry.Clamp(point, Bounds);
                Selection = SelectionGeometry.Rect(SelectionGeometry.Clamp(creating.Start, Bounds), end);
                break;
            case Moving moving:
                Selection = SelectionGeometry.Move(moving.Original, point.X - moving.Start.X, point.Y - moving.Start.Y, Bounds);
                break;
            case Resizing resizing:
                Selection = SelectionGeometry.Resize(resizing.Original, resizing.Handle, SelectionGeometry.Clamp(point, Bounds));
                break;
        }
    }

    /// <summary>松开鼠标。<paramref name="highlighted"/> 是鼠标下高亮的窗口或控件:没有选区时单击就选中它。</summary>
    public void MouseUp(PxRect? highlighted)
    {
        var drag = _drag;
        _drag = null;
        if (drag is not Creating creating || _didDragOut)
        {
            DropEmptySelection();
            return;
        }

        if (creating.Previous is { } previous)
        {
            Selection = previous;
        }
        else if (highlighted is { } rect)
        {
            Bounds = _pendingBounds;
            _scale = _pendingScale;
            Selection = SelectionGeometry.Clamp(rect, Bounds);
            DropEmptySelection();
        }
    }

    /// <summary>右键 / Esc:有选区时取消选区,没有时退出。</summary>
    public Effect Cancel()
    {
        _drag = null;
        if (Selection is null)
        {
            return Effect.Exit;
        }

        Selection = null;
        return Effect.None;
    }

    public void Select(PxRect rect, PxRect displayBounds, double scale)
    {
        _drag = null;
        Bounds = displayBounds;
        _scale = scale;
        Selection = SelectionGeometry.Clamp(rect, displayBounds);
        DropEmptySelection();
    }

    public void Nudge(int dx, int dy, bool resize)
    {
        if (Selection is { } selection && _drag is null)
        {
            Selection = SelectionGeometry.Nudge(selection, dx, dy, resize, Bounds);
        }
    }

    private void DropEmptySelection()
    {
        if (Selection is { } selection && (selection.Width < 1 || selection.Height < 1))
        {
            Selection = null;
        }
    }
}
