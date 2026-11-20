using System.Text.Json.Serialization;

namespace Waio.Feature.Screenshot.Domain.Annotation;

/// <summary>浮点坐标的点(全局物理像素;画笔、箭头需要小数)。</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD p, VectorD v) => new(p.X + v.Dx, p.Y + v.Dy);
}

public readonly record struct VectorD(double Dx, double Dy);

/// <summary>浮点矩形(对应 CGRect)。宽高可以是 0;不会是负数。</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double MidX => X + Width / 2;

    public double MidY => Y + Height / 2;

    /// <summary>两个对角点围成的矩形(MAIO SelectionGeometry.rect(from:to:))。</summary>
    public static RectD FromPoints(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    /// <summary>四边各向外扩 <paramref name="dx"/>、<paramref name="dy"/>(负数是向内收);收过头返回 null(CGRect.insetBy 的 null)。</summary>
    public RectD? Inflate(double dx, double dy) =>
        Width + dx * 2 < 0 || Height + dy * 2 < 0 ? null : new RectD(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);

    public RectD Union(PointD p) => FromEdges(Math.Min(X, p.X), Math.Min(Y, p.Y), Math.Max(Right, p.X), Math.Max(Bottom, p.Y));

    public static RectD FromEdges(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);

    public RectD Offset(VectorD v) => this with { X = X + v.Dx, Y = Y + v.Dy };

    public PxRect ToPx() => PxRect.FromEdges((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Ceiling(Right), (int)Math.Ceiling(Bottom));

    public static RectD From(PxRect r) => new(r.X, r.Y, r.Width, r.Height);
}

/// <summary>标注颜色(sRGB,0~1;MAIO AnnotationColor)。</summary>
public readonly record struct AnnotationColor(double Red, double Green, double Blue, double Alpha = 1)
{
    public double Luminance => 0.299 * Red + 0.587 * Green + 0.114 * Blue;

    /// <summary>预设色的名字(提示用)。</summary>
    public string Name => Array.IndexOf(AnnotationColors.Presets, this) switch
    {
        0 => "红色",
        1 => "橙色",
        2 => "黄色",
        3 => "绿色",
        4 => "蓝色",
        5 => "紫色",
        6 => "黑色",
        7 => "白色",
        _ => "颜色",
    };
}

/// <summary>预设颜色(MAIO AnnotationColor 的 static 成员)。</summary>
public static class AnnotationColors
{
    public static readonly AnnotationColor Red = new(0.93, 0.23, 0.21);
    public static readonly AnnotationColor Orange = new(1, 0.58, 0);
    public static readonly AnnotationColor Yellow = new(1, 0.84, 0.04);
    public static readonly AnnotationColor Green = new(0.2, 0.78, 0.35);
    public static readonly AnnotationColor Blue = new(0, 0.48, 1);
    public static readonly AnnotationColor Purple = new(0.69, 0.32, 0.87);
    public static readonly AnnotationColor Black = new(0, 0, 0);
    public static readonly AnnotationColor White = new(1, 1, 1);

    /// <summary>工具栏上的 8 个预设色。</summary>
    public static readonly AnnotationColor[] Presets = [Red, Orange, Yellow, Green, Blue, Purple, Black, White];
}

/// <summary>文字标注的外观(三者互斥)。文字颜色是标注的颜色;描边和底色用 <see cref="TextBox.BackgroundColor"/>。</summary>
public enum TextDecoration
{
    Plain,
    /// <summary>文字外面一圈描边,复杂背景上也看得清。</summary>
    Outline,
    /// <summary>文字下面垫一个颜色块。</summary>
    Background,
}

/// <summary>文字标注(MAIO TextBox)。<c>Size</c> 由界面按字体测量后填入。</summary>
public sealed record TextBox(PointD Origin, string Text, double FontSize, bool Bold, TextDecoration Decoration)
{
    /// <summary>描边或底色的颜色。旧数据没有时按文字颜色自动取黑或白。</summary>
    public AnnotationColor? BackgroundColor { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    [JsonIgnore]
    public RectD Frame => new(Origin.X, Origin.Y, Width, Height);
}

/// <summary>
/// 标注的形状(MAIO AnnotationShape)。坐标单位是物理像素:截图过程中是全局坐标,存入历史时换成相对截图左上角的坐标。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RectangleShape), "rectangle")]
[JsonDerivedType(typeof(EllipseShape), "ellipse")]
[JsonDerivedType(typeof(ArrowShape), "arrow")]
[JsonDerivedType(typeof(LineShape), "line")]
[JsonDerivedType(typeof(PenShape), "pen")]
[JsonDerivedType(typeof(HighlighterShape), "highlighter")]
[JsonDerivedType(typeof(TextShape), "text")]
[JsonDerivedType(typeof(MosaicShape), "mosaic")]
[JsonDerivedType(typeof(BlurShape), "blur")]
[JsonDerivedType(typeof(CounterShape), "counter")]
public abstract record AnnotationShape;

public sealed record RectangleShape(RectD Frame, bool Filled, bool Rounded) : AnnotationShape;

public sealed record EllipseShape(RectD Frame, bool Filled) : AnnotationShape;

public sealed record ArrowShape(PointD From, PointD To) : AnnotationShape;

public sealed record LineShape(PointD From, PointD To, bool Dashed) : AnnotationShape;

public sealed record PenShape(IReadOnlyList<PointD> Points) : AnnotationShape
{
    public bool Equals(PenShape? other) => other is not null && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => Points.Count;
}

public sealed record HighlighterShape(IReadOnlyList<PointD> Points) : AnnotationShape
{
    public bool Equals(HighlighterShape? other) => other is not null && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => Points.Count;
}

public sealed record TextShape(TextBox Box) : AnnotationShape;

/// <summary>马赛克:不可逆,每块取平均色(MAIO spec 4.2,参考 Flameshot 的安全打码)。</summary>
public sealed record MosaicShape(RectD Frame, double BlockSize) : AnnotationShape;

public sealed record BlurShape(RectD Frame, double Radius) : AnnotationShape;

public sealed record CounterShape(PointD Center, int Number, double Radius) : AnnotationShape;

/// <summary>一个可以再次选中、移动、修改的标注对象(MAIO Annotation)。</summary>
public sealed record Annotation(AnnotationShape Shape, AnnotationColor Color, double LineWidth)
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>平移并缩放(存入历史时把全局坐标换成图片坐标;移动对象时 scale = 1)。Id 不变。</summary>
    public Annotation Transformed(VectorD offset, double scale)
    {
        PointD P(PointD p) => new((p.X + offset.Dx) * scale, (p.Y + offset.Dy) * scale);
        RectD R(RectD r) => new((r.X + offset.Dx) * scale, (r.Y + offset.Dy) * scale, r.Width * scale, r.Height * scale);
        AnnotationShape shape = Shape switch
        {
            RectangleShape s => s with { Frame = R(s.Frame) },
            EllipseShape s => s with { Frame = R(s.Frame) },
            ArrowShape s => new ArrowShape(P(s.From), P(s.To)),
            LineShape s => s with { From = P(s.From), To = P(s.To) },
            PenShape s => new PenShape([.. s.Points.Select(P)]),
            HighlighterShape s => new HighlighterShape([.. s.Points.Select(P)]),
            TextShape s => new TextShape(s.Box with
            {
                Origin = P(s.Box.Origin), FontSize = s.Box.FontSize * scale, Width = s.Box.Width * scale, Height = s.Box.Height * scale,
            }),
            MosaicShape s => new MosaicShape(R(s.Frame), s.BlockSize * scale),
            BlurShape s => new BlurShape(R(s.Frame), s.Radius * scale),
            CounterShape s => s with { Center = P(s.Center), Radius = s.Radius * scale },
            _ => Shape,
        };
        return this with { Shape = shape, LineWidth = LineWidth * scale };
    }

    public Annotation Moved(VectorD delta) => Transformed(delta, 1);

    /// <summary>马赛克、模糊:只处理截图本身,先画;其他标注始终画在它们上面。</summary>
    [JsonIgnore]
    public bool IsEffect => Shape is MosaicShape or BlurShape;
}
