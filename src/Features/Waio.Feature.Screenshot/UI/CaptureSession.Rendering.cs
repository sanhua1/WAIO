using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>识别鼠标下的窗口和控件、取色、指针形状,以及把状态画到每个显示器的遮罩上(MAIO CaptureSession+Detection / +Rendering)。</summary>
internal sealed partial class CaptureSession
{
    /// <summary>控件查询一次最多用多久;超时就用已经找到的那一层。</summary>
    private static readonly TimeSpan ElementBudget = TimeSpan.FromMilliseconds(300);

    private ElementRequest? _pendingElement;
    /// <summary>工具栏在屏幕上的位置(全局物理像素);不显示时为 null。指针在上面时显示普通箭头。</summary>
    private PxRect? _toolbarRect;
    private Task? _elementQuery;

    private sealed record ElementRequest(PxPoint Point, nint Window, PxRect Clip);

    private bool DetectsControls => _options.AlwaysDetectControls || _ctrlHeld;

    /// <summary>没有选区时高亮鼠标下的窗口;鼠标下没有窗口(桌面)时高亮整个屏幕(MAIO updateHover)。</summary>
    private void UpdateHover()
    {
        if (_interaction.Selection is not null || _interaction.IsDragging || DisplayAt(_cursor) is not { } display)
        {
            _highlight = null;
            return;
        }

        var bounds = display.Display.Bounds;
        var window = WindowPicker.WindowAt(_cursor, _windows);
        var windowRect = window is null ? bounds : window.Frame.Intersect(bounds);
        _highlight = windowRect;
        if (window is not null && DetectsControls)
        {
            RequestElement(new ElementRequest(_cursor, window.Handle, windowRect));
        }
    }

    /// <summary>只保留最新一次查询:查询进行中又移动了鼠标,就等这次结束后查最新的位置(MAIO requestElement)。</summary>
    private void RequestElement(ElementRequest request)
    {
        _pendingElement = request;
        _elementQuery ??= RunElementQueriesAsync();
    }

    private async Task RunElementQueriesAsync()
    {
        try
        {
            while (_pendingElement is { } request && !_finished)
            {
                _pendingElement = null;
                var rect = await ScreenLocator.ControlRectAsync(request.Window, new ScreenPoint(request.Point.X, request.Point.Y), ElementBudget);
                if (_finished || _pendingElement is not null || _cursor != request.Point || !DetectsControls
                    || _interaction.Selection is not null || _interaction.IsDragging || rect is not { } found)
                {
                    continue;
                }

                var clipped = PxRect.FromEdges(found.Left, found.Top, found.Right, found.Bottom).Intersect(request.Clip);
                if (!clipped.IsEmpty)
                {
                    _highlight = clipped;
                    Render();
                }
            }
        }
        finally
        {
            _elementQuery = null;
        }
    }

    private void UpdateColor()
    {
        if (DisplayAt(_cursor) is { } display)
        {
            var b = display.Display.Bounds;
            _color = display.Image.ColorAt(_cursor.X - b.X, _cursor.Y - b.Y);
        }
    }

    private void UpdateCursorShape()
    {
        var shape = CursorShape.Crosshair;
        if (_toolbarRect is { } bar && bar.Contains(_cursor) && !_interaction.IsDragging)
        {
            shape = CursorShape.Arrow; // MAIO:工具栏上还原成普通指针
        }
        else if (_interaction.Selection is { } annotated && AnnotationCursor(annotated) is { } annotationShape)
        {
            shape = annotationShape;
        }
        else if (_edit is not null)
        {
            shape = CursorShape.Arrow; // 重新编辑时选区不能移动、调整
        }
        else if (_interaction.Selection is { } selection)
        {
            if (_interaction.CurrentDrag is CaptureInteraction.Moving)
            {
                shape = CursorShape.Move;
            }
            else if (_interaction.CurrentDrag is CaptureInteraction.Resizing resizing)
            {
                shape = Shape(resizing.Handle);
            }
            else if (!_interaction.IsDragging && SelectionGeometry.Handle(_cursor, selection, _interaction.HandleTolerance) is { } handle)
            {
                shape = Shape(handle);
            }
            else if (!_interaction.IsDragging && selection.Contains(_cursor))
            {
                shape = CursorShape.Move;
            }
        }

        foreach (var overlay in _overlays)
        {
            overlay.Surface.SetShape(shape);
        }
    }

    private static CursorShape Shape(SelectionHandle handle) => handle switch
    {
        SelectionHandle.TopLeft or SelectionHandle.BottomRight => CursorShape.ResizeNwse,
        SelectionHandle.TopRight or SelectionHandle.BottomLeft => CursorShape.ResizeNesw,
        SelectionHandle.Top or SelectionHandle.Bottom => CursorShape.ResizeNs,
        _ => CursorShape.ResizeWe,
    };

    /// <summary>把当前状态画到每个显示器的遮罩上(MAIO render)。</summary>
    private void Render()
    {
        if (_finished)
        {
            return;
        }

        _toolbarRect = null;
        for (var i = 0; i < _overlays.Count; i++)
        {
            _overlays[i].Render(FrameFor(_overlays[i], _frozen[i].Display.Bounds, _frozen[i].Display.Scale));
        }
    }

    /// <summary>一个显示器上的选区(或高亮的窗口、控件)、手柄、尺寸标签、放大镜。</summary>
    private OverlayFrame FrameFor(OverlayWindow overlay, PxRect bounds, double scale)
    {
        PxRect? target = null;
        var showsHandles = false;
        if (_interaction.Selection is { } selection)
        {
            target = bounds == _interaction.Bounds ? selection : null;
            showsHandles = !_interaction.IsCreating && _edit is null; // 重新编辑时选区固定,没有手柄
        }
        else if (_highlight is { } highlight && bounds.Intersects(highlight))
        {
            target = highlight.Intersect(bounds);
        }

        var magnifier = MagnifierFor(bounds, scale);
        if (target is not { } rect)
        {
            return OverlayFrame.Empty with { Magnifier = magnifier };
        }

        var local = rect.Offset(-bounds.X, -bounds.Y);
        var handles = showsHandles ? SelectionHandles.All.Select(h => h.Point(local)).ToArray() : [];
        var text = _edit is { } edit ? $"{edit.Source.Image.Width} × {edit.Source.Image.Height}" : $"{rect.Width} × {rect.Height}";
        var (width, height) = overlay.MeasureBadge(text);
        var label = OverlayLayout.SizeLabelFrame(width, height, rect, bounds, scale).Offset(-bounds.X, -bounds.Y);
        var toolbar = ToolbarFor(overlay, bounds, scale);
        return new OverlayFrame(local, handles, text, label, magnifier,
            toolbar, toolbar is null ? null : _toolbarState, SceneFor(bounds));
    }

    /// <summary>
    /// 工具栏:选区确定(不在拖动)时,放在选区所在显示器上——优先选区下方,放不下依次换到上方、内部(MAIO placeToolbar)。
    /// 返回本显示器内的局部坐标,同时记下全局位置。
    /// </summary>
    private PxPoint? ToolbarFor(OverlayWindow overlay, PxRect bounds, double scale)
    {
        if (_interaction.Selection is not { } selection || _interaction.IsDragging || _editor.IsDragging || bounds != _interaction.Bounds)
        {
            return null;
        }

        if (_toolbarState is null)
        {
            SyncToolbar();
        }

        var (width, height) = overlay.MeasureToolbar();
        var frame = OverlayLayout.ToolbarFrame(width, height, selection, bounds, scale);
        _toolbarRect = frame;
        return new PxPoint(frame.X - bounds.X, frame.Y - bounds.Y);
    }

    /// <summary>放大镜:没有选区时,或者正在拖动时,在鼠标所在的显示器上显示(MAIO renderMagnifier)。</summary>
    private MagnifierState? MagnifierFor(PxRect bounds, double scale)
    {
        // 开始标注后不再显示(spec 4.1)
        var visible = _options.ShowMagnifier && _edit is null && bounds.Contains(_cursor) && _editor.Document.IsEmpty && !_editor.IsDragging
            && !_textField.IsEditing && (_interaction.Selection is null || _interaction.IsDragging);
        if (!visible)
        {
            return null;
        }

        var x = _cursor.X - bounds.X;
        var y = _cursor.Y - bounds.Y;
        var colorText = _color is { } c ? _options.ColorFormat.Format(c) : "-";
        var (width, height) = MagnifierView.SizeFor(scale);
        var frame = OverlayLayout.MagnifierFrame(width, height, _cursor, bounds, scale).Offset(-bounds.X, -bounds.Y);
        return new MagnifierState(x, y, [$"{x}, {y}", colorText, "C 复制 · Shift 切换"], _color, frame);
    }

    /// <summary>
    /// 选区里的标注(MAIO renderAnnotations):底图 + 对象,正在输入的文字隐藏原来那一份、换成输入框和它的描边 / 底色预览;
    /// 选中的对象画虚线框和手柄。没有标注、也没在输入文字时返回 null(不画,直接看冻结画面)。
    /// </summary>
    private AnnotationScene? SceneFor(PxRect bounds)
    {
        if (_interaction.Selection is not { } selection || bounds != _interaction.Bounds || Source is not { } source)
        {
            return null;
        }

        var editingId = _textField.IsEditing ? _textField.EditingId : null;
        var items = _editor.VisibleItems;
        if (items.Count == 0 && !_textField.IsEditing && _edit is null) // 重新编辑时底图是原图,总要画
        {
            return null;
        }

        if (editingId is { } hidden)
        {
            items = [.. items.Where(a => a.Id != hidden)];
        }

        (TextBox, AnnotationColor)? preview = _textField.CurrentBox is { Decoration: not TextDecoration.Plain } box ? (box, _textField.Color) : null;
        RectD? outline = null;
        IReadOnlyList<PointD> handles = [];
        if (_editor.Document.Selected is { } selected && _editor.Drag is not AnnotationEditor.Drawing && !_textField.IsEditing)
        {
            outline = selected.Bounds();
            handles = [.. selected.Handles().Select(h => h.Point)];
        }

        return new AnnotationScene(items, RectD.From(selection), source, _effectCache, outline, handles, preview);
    }

    /// <summary>标注相关的指针形状(MAIO updateCursorShape 的前半段);不归标注管时返回 null。</summary>
    private CursorShape? AnnotationCursor(PxRect selection)
    {
        if (_interaction.IsDragging)
        {
            return null;
        }

        var point = new PointD(_cursor.X, _cursor.Y);
        if (_editor.Document.Selected is { } selected && selected.HandleAt(point, _editor.HandleTolerance) is { } handle)
        {
            return handle is AnnotationHandle.Frame frame ? Shape(frame.Side) : CursorShape.Crosshair;
        }

        if (_edit is null && SelectionGeometry.Handle(_cursor, selection, _interaction.HandleTolerance) is not null)
        {
            return null;
        }

        if (_editor.Document.Selected?.Contains(point, _editor.HitTolerance) == true
            || (_editor.Tool is null && _editor.Document.Hit(point, _editor.HitTolerance) is not null))
        {
            return CursorShape.Hand;
        }

        if (_editor.Tool is { } tool && selection.Contains(_cursor))
        {
            return tool == AnnotationTool.Text ? CursorShape.IBeam : CursorShape.Crosshair;
        }

        return null;
    }
}
