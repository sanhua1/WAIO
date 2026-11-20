namespace Waio.Feature.Screenshot.Domain.Annotation;

/// <summary>
/// 标注编辑的鼠标状态机(MAIO AnnotationEditor,纯逻辑,可单测):画新对象、选中、移动、拖手柄、改样式(<c>.Style.cs</c>)。
/// 坐标是全局物理像素。工具大小(<see cref="ToolStyle"/>)单位是 pt,画到图上时乘 <see cref="Scale"/>(选区所在显示器的缩放比例)。
/// </summary>
public sealed partial class AnnotationEditor
{
    public abstract record DragState;

    public sealed record Drawing(PointD Start) : DragState;

    public sealed record Moving(Annotation Original, PointD Start, bool Saved) : DragState;

    public sealed record Resizing(AnnotationHandle Handle, Annotation Original, bool Saved) : DragState;

    /// <summary>鼠标按下的结果。</summary>
    public abstract record Outcome
    {
        /// <summary>不归标注处理(交给选区:移动、调整、新建)。</summary>
        public sealed record NotHandled : Outcome;

        public sealed record Handled : Outcome;

        /// <summary>在这里开始输入新文字。</summary>
        public sealed record BeginText(PointD Point) : Outcome;

        /// <summary>重新编辑这条文字。</summary>
        public sealed record EditText(Guid Id) : Outcome;
    }

    public AnnotationDocument Document { get; } = new();

    /// <summary>当前工具;null 表示没有选工具(单击选中对象)。</summary>
    public AnnotationTool? Tool { get; private set; }

    /// <summary>文字工具:色板改文字颜色还是背景色。外观为"无"时只能改文字颜色。</summary>
    public TextColorTarget TextColorTarget { get; set; } = TextColorTarget.Foreground;

    public ToolStyle Style { get; set; } = new();

    /// <summary>每 pt 的物理像素数(选区所在显示器的缩放比例)。</summary>
    public double Scale { get; set; } = 1;

    /// <summary>正在画的对象(松开鼠标后才加入文档)。</summary>
    public Annotation? Draft { get; private set; }

    public DragState? Drag { get; private set; }

    public bool IsDragging => Drag is not null;

    /// <summary>点击判定的容差(4 pt)。</summary>
    public double HitTolerance => 4 * Scale;

    /// <summary>手柄的点击容差(7 pt,同选区手柄)。</summary>
    public double HandleTolerance => SelectionGeometry.HandleToleranceDip * Scale;

    /// <summary>工具栏显示哪个工具的选项:选中的对象优先,其次是当前工具。</summary>
    public AnnotationTool? ActiveTool => Document.Selected?.Tool() ?? Tool;

    /// <summary>所有要显示的对象(包括正在画的)。</summary>
    public IReadOnlyList<Annotation> VisibleItems => Draft is { } draft ? [.. Document.Items, draft] : Document.Items;

    public Outcome MouseDown(PointD point, int clickCount, RectD selection)
    {
        if (Document.Selected is { } selected)
        {
            if (selected.HandleAt(point, HandleTolerance) is { } handle)
            {
                Drag = new Resizing(handle, selected, false);
                return new Outcome.Handled();
            }

            if (selected.Contains(point, HitTolerance))
            {
                // 双击文字,或者文字工具下单击文字:重新编辑
                if ((clickCount >= 2 || Tool == AnnotationTool.Text) && selected.Shape is TextShape)
                {
                    return new Outcome.EditText(selected.Id);
                }

                Drag = new Moving(selected, point, false);
                return new Outcome.Handled();
            }
        }

        if (clickCount >= 2 && Document.Hit(point, HitTolerance) is { Shape: TextShape } doubleClicked)
        {
            Document.SelectedId = doubleClicked.Id;
            return new Outcome.EditText(doubleClicked.Id);
        }

        if (Tool is not { } tool)
        {
            if (Document.Hit(point, HitTolerance) is { } hit)
            {
                Document.SelectedId = hit.Id;
                Drag = new Moving(hit, point, false);
                return new Outcome.Handled();
            }

            Document.SelectedId = null;
            return new Outcome.NotHandled();
        }

        // 文字工具单击已有的文字:重新编辑
        if (tool == AnnotationTool.Text && Document.Hit(point, HitTolerance) is { Shape: TextShape } text)
        {
            Document.SelectedId = text.Id;
            return new Outcome.EditText(text.Id);
        }

        Document.SelectedId = null;
        return selection.Contains(point) ? Begin(tool, point) : new Outcome.NotHandled();
    }

    public void MouseDragged(PointD point, bool constrained)
    {
        switch (Drag)
        {
            case Drawing drawing when Draft is { } draft:
                Draft = UpdateDraft(draft, drawing.Start, point, constrained);
                break;
            case Moving moving:
                if (!moving.Saved)
                {
                    Document.Checkpoint();
                    Drag = moving with { Saved = true };
                }

                Document.Replace(moving.Original.Moved(new VectorD(point.X - moving.Start.X, point.Y - moving.Start.Y)));
                break;
            case Resizing resizing:
                if (!resizing.Saved)
                {
                    Document.Checkpoint();
                    Drag = resizing with { Saved = true };
                }

                var target = constrained ? ConstrainedPoint(resizing.Original, resizing.Handle, point) : point;
                Document.Replace(resizing.Original.Resized(resizing.Handle, target));
                break;
        }
    }

    public void MouseUp()
    {
        var drag = Drag;
        var draft = Draft;
        Drag = null;
        Draft = null;
        if (drag is Drawing && draft is not null && draft.IsMeaningful(2 * Scale))
        {
            Document.Add(draft);
            Document.SelectedId = draft.Id; // 画完自动选中,方便马上改颜色、粗细
        }
    }

    /// <summary>选择工具;再选一次同一个工具表示取消。</summary>
    public void SelectTool(AnnotationTool? tool)
    {
        Tool = tool == Tool ? null : tool;
        Document.SelectedId = null;
    }

    public bool DeleteSelected()
    {
        if (Document.SelectedId is not { } id)
        {
            return false;
        }

        Document.Remove(id);
        return true;
    }

    /// <summary>Esc 的前两层:取消选中对象 → 取消工具。返回 false 表示这一层没有可退出的。</summary>
    public bool Escape()
    {
        if (Document.SelectedId is not null)
        {
            Document.SelectedId = null;
            return true;
        }

        if (Tool is not null)
        {
            Tool = null;
            return true;
        }

        return false;
    }

    public void AdjustCounter(int delta) =>
        Document.CounterAdjustment = Math.Max(Document.CounterAdjustment + delta, -Document.HighestCounterNumber);

    private Outcome Begin(AnnotationTool tool, PointD point)
    {
        var color = Style.ColorFor(tool);
        var size = Style.Size(tool) * Scale;
        switch (tool)
        {
            case AnnotationTool.Text:
                return new Outcome.BeginText(point);
            case AnnotationTool.Counter:
                Document.Add(new Annotation(new CounterShape(point, Document.NextCounterNumber, size), color, 0));
                Document.CounterAdjustment = 0;
                return new Outcome.Handled();
        }

        var empty = new RectD(point.X, point.Y, 0, 0);
        AnnotationShape shape = tool switch
        {
            AnnotationTool.Rectangle => new RectangleShape(empty, Style.Filled, Style.Rounded),
            AnnotationTool.Ellipse => new EllipseShape(empty, Style.Filled),
            AnnotationTool.Arrow => new ArrowShape(point, point),
            AnnotationTool.Line => new LineShape(point, point, Style.Dashed),
            AnnotationTool.Pen => new PenShape([point]),
            AnnotationTool.Highlighter => new HighlighterShape([point]),
            AnnotationTool.Mosaic => new MosaicShape(empty, size),
            _ => new BlurShape(empty, size),
        };
        Draft = new Annotation(shape, color, size);
        Drag = new Drawing(point);
        return new Outcome.Handled();
    }

    private Annotation UpdateDraft(Annotation draft, PointD start, PointD point, bool constrained)
    {
        var squared = constrained ? Snapping.Square(start, point) : point;
        var angled = constrained ? Snapping.Angle(start, point) : point;
        var frame = RectD.FromPoints(start, squared);
        AnnotationShape shape = draft.Shape switch
        {
            RectangleShape s => s with { Frame = frame },
            EllipseShape s => s with { Frame = frame },
            MosaicShape s => s with { Frame = RectD.FromPoints(start, point) },
            BlurShape s => s with { Frame = RectD.FromPoints(start, point) },
            ArrowShape => new ArrowShape(start, angled),
            LineShape s => s with { From = start, To = angled },
            PenShape s => new PenShape(Appending(point, s.Points)),
            // Shift 画水平直线
            HighlighterShape s => new HighlighterShape(constrained ? [start, new PointD(point.X, start.Y)] : Appending(point, s.Points)),
            _ => draft.Shape,
        };
        return draft with { Shape = shape };
    }

    /// <summary>拖手柄时按住 Shift:直线、箭头吸附角度。</summary>
    private static PointD ConstrainedPoint(Annotation annotation, AnnotationHandle handle, PointD point) => (annotation.Shape, handle) switch
    {
        (ArrowShape s, AnnotationHandle.End) => Snapping.Angle(s.From, point),
        (LineShape s, AnnotationHandle.End) => Snapping.Angle(s.From, point),
        (ArrowShape s, AnnotationHandle.Start) => Snapping.Angle(s.To, point),
        (LineShape s, AnnotationHandle.Start) => Snapping.Angle(s.To, point),
        _ => point,
    };

    /// <summary>画笔:和上一个点距离太近(不到 1 pt)时不加,减少点数;路径平滑交给渲染。</summary>
    private IReadOnlyList<PointD> Appending(PointD point, IReadOnlyList<PointD> points) =>
        points.Count > 0 && AnnotationGeometry.Distance(points[^1], point) < Scale ? points : [.. points, point];

    /// <summary>修改选中的对象(一步撤销);<paramref name="coalescing"/> 相同的连续修改合并。</summary>
    public void UpdateSelected(Func<Annotation, Annotation> change, string? coalescing = null)
    {
        if (Document.Selected is not { } before)
        {
            return;
        }

        var after = change(before);
        if (after == before)
        {
            return;
        }

        Document.Checkpoint(coalescing is null ? null : $"{coalescing}-{after.Id}");
        Document.Replace(after);
    }
}
