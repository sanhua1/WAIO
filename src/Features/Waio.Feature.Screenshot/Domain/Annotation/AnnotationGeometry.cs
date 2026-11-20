namespace Waio.Feature.Screenshot.Domain.Annotation;

/// <summary>选中的标注上可以拖动的手柄(MAIO AnnotationHandle)。</summary>
public abstract record AnnotationHandle
{
    /// <summary>矩形类(矩形、椭圆、马赛克、模糊)的 8 个手柄。</summary>
    public sealed record Frame(SelectionHandle Side) : AnnotationHandle;

    /// <summary>直线、箭头的起点。</summary>
    public sealed record Start : AnnotationHandle;

    /// <summary>直线、箭头的终点。</summary>
    public sealed record End : AnnotationHandle;
}

/// <summary>标注的包围盒、点击判定、移动、调整(MAIO AnnotationGeometry)。距离、容差都是物理像素,由调用方按缩放比例换算好。</summary>
public static class AnnotationGeometry
{
    /// <summary>对应的工具(选中对象后用来决定工具栏显示哪些选项)。</summary>
    public static AnnotationTool Tool(this Annotation a) => a.Shape switch
    {
        RectangleShape => AnnotationTool.Rectangle,
        EllipseShape => AnnotationTool.Ellipse,
        ArrowShape => AnnotationTool.Arrow,
        LineShape => AnnotationTool.Line,
        PenShape => AnnotationTool.Pen,
        HighlighterShape => AnnotationTool.Highlighter,
        TextShape => AnnotationTool.Text,
        MosaicShape => AnnotationTool.Mosaic,
        BlurShape => AnnotationTool.Blur,
        _ => AnnotationTool.Counter,
    };

    public static RectD Bounds(this Annotation a)
    {
        var half = a.LineWidth / 2;
        return a.Shape switch
        {
            RectangleShape s => s.Frame.Inflate(half, half)!.Value,
            EllipseShape s => s.Frame.Inflate(half, half)!.Value,
            MosaicShape s => s.Frame,
            BlurShape s => s.Frame,
            ArrowShape s => RectD.FromPoints(s.From, s.To).Inflate(a.LineWidth * 2, a.LineWidth * 2)!.Value,
            LineShape s => RectD.FromPoints(s.From, s.To).Inflate(a.LineWidth * 2, a.LineWidth * 2)!.Value,
            PenShape s => BoundingBox(s.Points).Inflate(half, half)!.Value,
            HighlighterShape s => BoundingBox(s.Points).Inflate(half, half)!.Value,
            TextShape s => s.Box.Frame,
            CounterShape s => new RectD(s.Center.X - s.Radius, s.Center.Y - s.Radius, s.Radius * 2, s.Radius * 2),
            _ => default,
        };
    }

    /// <summary>点击判定:线条类按到线的距离,实心和区域类按范围。</summary>
    public static bool Contains(this Annotation a, PointD point, double tolerance)
    {
        var reach = tolerance + a.LineWidth / 2;
        switch (a.Shape)
        {
            case RectangleShape s:
                var outer = s.Frame.Inflate(reach, reach)!.Value;
                var inner = s.Frame.Inflate(-reach, -reach);
                return outer.Contains(point) && (s.Filled || inner is not { } i || i.Width <= 0 || i.Height <= 0 || !i.Contains(point));
            case EllipseShape s:
                return EllipseContains(s.Frame, point, s.Filled, reach);
            case ArrowShape s:
                return DistanceToSegment(point, s.From, s.To) <= reach;
            case LineShape s:
                return DistanceToSegment(point, s.From, s.To) <= reach;
            case PenShape s:
                return DistanceToPolyline(point, s.Points) <= reach;
            case HighlighterShape s:
                return DistanceToPolyline(point, s.Points) <= reach;
            case CounterShape s:
                return Math.Sqrt(Math.Pow(point.X - s.Center.X, 2) + Math.Pow(point.Y - s.Center.Y, 2)) <= s.Radius + tolerance;
            default: // 文字、马赛克、模糊
                return a.Bounds().Inflate(tolerance, tolerance)!.Value.Contains(point);
        }
    }

    public static IReadOnlyList<(AnnotationHandle Handle, PointD Point)> Handles(this Annotation a) => a.Shape switch
    {
        RectangleShape s => FrameHandles(s.Frame),
        EllipseShape s => FrameHandles(s.Frame),
        MosaicShape s => FrameHandles(s.Frame),
        BlurShape s => FrameHandles(s.Frame),
        ArrowShape s => [(new AnnotationHandle.Start(), s.From), (new AnnotationHandle.End(), s.To)],
        LineShape s => [(new AnnotationHandle.Start(), s.From), (new AnnotationHandle.End(), s.To)],
        _ => [],
    };

    public static AnnotationHandle? HandleAt(this Annotation a, PointD point, double tolerance) =>
        a.Handles().FirstOrDefault(h => Math.Abs(h.Point.X - point.X) <= tolerance && Math.Abs(h.Point.Y - point.Y) <= tolerance).Handle;

    /// <summary>拖动手柄后的对象(从按下时的原始对象计算)。</summary>
    public static Annotation Resized(this Annotation a, AnnotationHandle handle, PointD point)
    {
        AnnotationShape shape = (a.Shape, handle) switch
        {
            (RectangleShape s, AnnotationHandle.Frame f) => s with { Frame = Resize(s.Frame, f.Side, point) },
            (EllipseShape s, AnnotationHandle.Frame f) => s with { Frame = Resize(s.Frame, f.Side, point) },
            (MosaicShape s, AnnotationHandle.Frame f) => s with { Frame = Resize(s.Frame, f.Side, point) },
            (BlurShape s, AnnotationHandle.Frame f) => s with { Frame = Resize(s.Frame, f.Side, point) },
            (ArrowShape s, AnnotationHandle.Start) => s with { From = point },
            (ArrowShape s, AnnotationHandle.End) => s with { To = point },
            (LineShape s, AnnotationHandle.Start) => s with { From = point },
            (LineShape s, AnnotationHandle.End) => s with { To = point },
            _ => a.Shape,
        };
        return a with { Shape = shape };
    }

    /// <summary>太小的对象(手抖的单击)不保留。<paramref name="minimum"/> 是 2 pt 换算成的像素。</summary>
    public static bool IsMeaningful(this Annotation a, double minimum) => a.Shape switch
    {
        RectangleShape s => s.Frame.Width >= minimum && s.Frame.Height >= minimum,
        EllipseShape s => s.Frame.Width >= minimum && s.Frame.Height >= minimum,
        MosaicShape s => s.Frame.Width >= minimum && s.Frame.Height >= minimum,
        BlurShape s => s.Frame.Width >= minimum && s.Frame.Height >= minimum,
        ArrowShape s => Distance(s.From, s.To) >= minimum,
        LineShape s => Distance(s.From, s.To) >= minimum,
        PenShape s => s.Points.Count > 0,
        HighlighterShape s => s.Points.Count > 0,
        TextShape s => s.Box.Text.Length > 0,
        _ => true,
    };

    // ---- 几何辅助 ----

    public static PointD Point(this SelectionHandle handle, RectD r) => handle switch
    {
        SelectionHandle.TopLeft => new(r.X, r.Y),
        SelectionHandle.Top => new(r.MidX, r.Y),
        SelectionHandle.TopRight => new(r.Right, r.Y),
        SelectionHandle.Right => new(r.Right, r.MidY),
        SelectionHandle.BottomRight => new(r.Right, r.Bottom),
        SelectionHandle.Bottom => new(r.MidX, r.Bottom),
        SelectionHandle.BottomLeft => new(r.X, r.Bottom),
        _ => new(r.X, r.MidY),
    };

    /// <summary>拖手柄调整,拖过对边时翻转(MAIO SelectionGeometry.resize)。</summary>
    public static RectD Resize(RectD original, SelectionHandle handle, PointD point)
    {
        var (minX, maxX, minY, maxY) = (original.X, original.Right, original.Y, original.Bottom);
        if (handle.MovesLeft())
        {
            minX = point.X;
        }

        if (handle.MovesRight())
        {
            maxX = point.X;
        }

        if (handle.MovesTop())
        {
            minY = point.Y;
        }

        if (handle.MovesBottom())
        {
            maxY = point.Y;
        }

        return RectD.FromPoints(new PointD(minX, minY), new PointD(maxX, maxY));
    }

    public static RectD BoundingBox(IReadOnlyList<PointD> points)
    {
        if (points.Count == 0)
        {
            return default;
        }

        var rect = new RectD(points[0].X, points[0].Y, 0, 0);
        for (var i = 1; i < points.Count; i++)
        {
            rect = rect.Union(points[i]);
        }

        return rect;
    }

    public static double Distance(PointD a, PointD b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    public static double DistanceToSegment(PointD p, PointD start, PointD end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0)
        {
            return Distance(p, start);
        }

        var t = Math.Clamp(((p.X - start.X) * dx + (p.Y - start.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new PointD(start.X + t * dx, start.Y + t * dy));
    }

    public static double DistanceToPolyline(PointD p, IReadOnlyList<PointD> points)
    {
        if (points.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (points.Count == 1)
        {
            return Distance(p, points[0]);
        }

        var best = double.PositiveInfinity;
        for (var i = 1; i < points.Count; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, points[i - 1], points[i]));
        }

        return best;
    }

    private static (AnnotationHandle, PointD)[] FrameHandles(RectD frame) =>
        [.. SelectionHandles.All.Select(h => ((AnnotationHandle)new AnnotationHandle.Frame(h), h.Point(frame)))];

    private static bool EllipseContains(RectD frame, PointD p, bool filled, double reach)
    {
        var rx = frame.Width / 2;
        var ry = frame.Height / 2;
        if (rx <= 0 || ry <= 0)
        {
            return false;
        }

        var outer = Math.Pow((p.X - frame.MidX) / (rx + reach), 2) + Math.Pow((p.Y - frame.MidY) / (ry + reach), 2);
        if (outer > 1)
        {
            return false;
        }

        if (filled || rx <= reach || ry <= reach)
        {
            return true;
        }

        return Math.Pow((p.X - frame.MidX) / (rx - reach), 2) + Math.Pow((p.Y - frame.MidY) / (ry - reach), 2) >= 1;
    }
}

/// <summary>按住 Shift 时的吸附(MAIO Snapping,spec 3.2)。</summary>
public static class Snapping
{
    /// <summary>直线、箭头:方向吸附到 45° 的整数倍,长度不变。</summary>
    public static PointD Angle(PointD start, PointD end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0)
        {
            return end;
        }

        var step = Math.PI / 4;
        var angle = Math.Round(Math.Atan2(dy, dx) / step, MidpointRounding.AwayFromZero) * step;
        return new PointD(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
    }

    /// <summary>矩形、椭圆:变成正方形、正圆(边长取较长的一边)。</summary>
    public static PointD Square(PointD start, PointD end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new PointD(start.X + (dx < 0 ? -side : side), start.Y + (dy < 0 ? -side : side));
    }
}
