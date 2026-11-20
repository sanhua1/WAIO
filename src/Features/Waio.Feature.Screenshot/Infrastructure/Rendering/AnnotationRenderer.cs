using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Waio.Feature.Screenshot.Infrastructure.Rendering;

/// <summary>
/// 画标注(MAIO AnnotationRenderer)。屏幕上的显示和最终输出用同一套代码,保证"看到的就是得到的"(spec P2 验收)。
/// 约定:调用前绘图会话的坐标已经是"全局物理像素"(屏幕上由遮罩设置变换,输出时平移到选区左上角)。
/// 用 Win2D(Direct2D),ADR-0016。
/// </summary>
internal static class AnnotationRenderer
{
    /// <summary>底图(选区内的冻结画面)+ 全部标注,裁剪在选区里。</summary>
    public static void DrawComposite(
        CanvasDrawingSession ds, IReadOnlyList<Annotation> items, RectD selection, EffectSource source, EffectCache cache)
    {
        var pixels = source.PixelRect(selection);
        if (pixels.IsEmpty)
        {
            return;
        }

        var global = source.GlobalRect(pixels);
        using (ds.CreateLayer(1, ToRect(global)))
        {
            // 1:1 时最近邻(像素准确);重新编辑缩小显示的大图时用高质量插值
            var interpolation = source.Zoom == 1 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic;
            ds.DrawImage(source.Bitmap(ds), ToRect(global), ToRect(pixels), 1, interpolation);

            // 马赛克、模糊只处理截图本身,先画;其他标注始终画在它们上面,不会被模糊掉
            foreach (var annotation in items.Where(a => a.IsEffect).Concat(items.Where(a => !a.IsEffect)))
            {
                Draw(ds, annotation, source, cache);
            }
        }
    }

    /// <summary>输出:选区裁剪 + 标注,按物理像素(MAIO renderOutput)。</summary>
    public static PixelBuffer RenderOutput(IReadOnlyList<Annotation> items, PxRect selection, EffectSource source) =>
        Render(items, RectD.From(selection), source, selection.Width, selection.Height, 1);

    /// <summary>重新编辑的输出:按原图的分辨率输出整张图(屏幕上可能是缩小显示的,MAIO makeEditResult)。</summary>
    public static PixelBuffer RenderEditOutput(IReadOnlyList<Annotation> items, EffectSource source) =>
        Render(items, source.Bounds, source, source.Image.Width, source.Image.Height, source.Zoom);

    private static PixelBuffer Render(IReadOnlyList<Annotation> items, RectD selection, EffectSource source, int width, int height, double zoom)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        using var cache = new EffectCache();
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Rgba(255, 0, 0, 0));
            ds.Transform = Matrix3x2.CreateTranslation((float)-selection.X, (float)-selection.Y) * Matrix3x2.CreateScale((float)zoom);
            DrawComposite(ds, items, selection, source, cache);
        }

        var bytes = target.GetPixelBytes();
        PixelBuffer.MakeOpaque(bytes);
        return new PixelBuffer(width, height, bytes);
    }

    /// <summary>画一个对象。</summary>
    public static void Draw(CanvasDrawingSession ds, Annotation annotation, EffectSource source, EffectCache cache)
    {
        var color = ToColor(annotation.Color);
        var width = (float)annotation.LineWidth;
        var scale = source.Scale;
        switch (annotation.Shape)
        {
            case RectangleShape s:
                var frame = ToRect(s.Frame);
                if (s.Rounded)
                {
                    var radius = (float)Math.Min(6 * scale + annotation.LineWidth, Math.Min(s.Frame.Width / 4, s.Frame.Height / 4));
                    if (s.Filled)
                    {
                        ds.FillRoundedRectangle(frame, radius, radius, color);
                    }
                    else
                    {
                        using var style = Stroke(CanvasLineJoin.Round);
                        ds.DrawRoundedRectangle(frame, radius, radius, color, width, style);
                    }
                }
                else if (s.Filled)
                {
                    ds.FillRectangle(frame, color);
                }
                else
                {
                    using var style = Stroke(CanvasLineJoin.Miter);
                    ds.DrawRectangle(frame, color, width, style);
                }

                break;
            case EllipseShape s:
                var center = new Vector2((float)s.Frame.MidX, (float)s.Frame.MidY);
                var (rx, ry) = ((float)(s.Frame.Width / 2), (float)(s.Frame.Height / 2));
                if (s.Filled)
                {
                    ds.FillEllipse(center, rx, ry, color);
                }
                else
                {
                    ds.DrawEllipse(center, rx, ry, color, width);
                }

                break;
            case LineShape s:
                using (var style = Stroke(CanvasLineJoin.Round))
                {
                    if (s.Dashed)
                    {
                        style.CustomDashStyle = [3f, 2.5f]; // 单位是线宽(MAIO:线宽 × 3、× 2.5)
                    }

                    ds.DrawLine(V(s.From), V(s.To), color, width, style);
                }

                break;
            case ArrowShape s:
                DrawArrow(ds, s.From, s.To, annotation.LineWidth, color);
                break;
            case PenShape s:
                DrawSmooth(ds, s.Points, color, width, CanvasCapStyle.Round);
                break;
            case HighlighterShape s:
                // MAIO 用正片叠底;Direct2D 的绘图会话没有正片叠底,用"变暗"(逐通道取较小值)代替:
                // 浅色荧光笔压在文字上,文字仍然清晰,视觉上几乎一样。关掉抗锯齿,避免半透明边缘被"取小值"变成黑边。
                var blend = ds.Blend;
                var antialias = ds.Antialiasing;
                ds.Blend = CanvasBlend.Min;
                ds.Antialiasing = CanvasAntialiasing.Aliased;
                DrawSmooth(ds, s.Points, color, width, CanvasCapStyle.Square);
                ds.Blend = blend;
                ds.Antialiasing = antialias;
                break;
            case TextShape s:
                TextRendering.Draw(ds, s.Box, annotation.Color, scale);
                break;
            case CounterShape s:
                DrawCounter(ds, s, annotation.Color);
                break;
            case MosaicShape s:
                if (cache.Mosaic(ds, s.Frame, s.BlockSize, source) is { } mosaic)
                {
                    ds.DrawImage(mosaic.Image, ToRect(mosaic.Rect), mosaic.Image.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
                }

                break;
            case BlurShape s:
                if (cache.Blur(ds, s.Frame, s.Radius, source) is { } blurred)
                {
                    ds.DrawImage(blurred.Image, ToRect(blurred.Rect), blurred.Image.Bounds, 1, CanvasImageInterpolation.Linear);
                }

                break;
        }
    }

    public static Windows.UI.Color ToColor(AnnotationColor c) =>
        Rgba(Byte(c.Alpha), Byte(c.Red), Byte(c.Green), Byte(c.Blue));

    /// <summary>不用 WinUI 的 ColorHelper / Colors:它们要 WinUI 运行时,渲染测试和后台输出用不了。</summary>
    public static Windows.UI.Color Rgba(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };

    public static Rect ToRect(RectD r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    public static Rect ToRect(PxRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Vector2 V(PointD p) => new((float)p.X, (float)p.Y);

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static CanvasStrokeStyle Stroke(CanvasLineJoin join) => new()
    {
        StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, DashCap = CanvasCapStyle.Round, LineJoin = join,
    };

    /// <summary>平滑路径:经过相邻两点中点的二次曲线(MAIO smoothPath)。</summary>
    private static void DrawSmooth(CanvasDrawingSession ds, IReadOnlyList<PointD> points, Windows.UI.Color color, float width, CanvasCapStyle cap)
    {
        if (points.Count == 0)
        {
            return;
        }

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(V(points[0]));
        if (points.Count <= 2)
        {
            builder.AddLine(V(points[^1]));
        }
        else
        {
            for (var i = 1; i < points.Count - 1; i++)
            {
                var control = points[i];
                var next = points[i + 1];
                builder.AddQuadraticBezier(V(control), new Vector2((float)((control.X + next.X) / 2), (float)((control.Y + next.Y) / 2)));
            }

            builder.AddLine(V(points[^1]));
        }

        builder.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        using var style = new CanvasStrokeStyle { StartCap = cap, EndCap = cap, LineJoin = CanvasLineJoin.Round };
        ds.DrawGeometry(geometry, color, width, style);
    }

    /// <summary>箭头:实心三角形箭头,大小随粗细变化(MAIO drawArrow)。</summary>
    private static void DrawArrow(CanvasDrawingSession ds, PointD from, PointD to, double width, Windows.UI.Color color)
    {
        var length = AnnotationGeometry.Distance(from, to);
        if (length <= 0)
        {
            return;
        }

        var headLength = Math.Min(Math.Max(10, width * 4), length);
        var headWidth = headLength * 0.85;
        var ux = (to.X - from.X) / length;
        var uy = (to.Y - from.Y) / length;
        var baseX = to.X - ux * headLength;
        var baseY = to.Y - uy * headLength;
        // 线身只画到箭头底部稍里面,避免圆头从箭头两侧露出来
        var shaftEnd = new PointD(to.X - ux * headLength * 0.6, to.Y - uy * headLength * 0.6);
        using (var style = Stroke(CanvasLineJoin.Round))
        {
            ds.DrawLine(V(from), V(shaftEnd), color, (float)width, style);
        }

        using var head = CanvasGeometry.CreatePolygon(ds,
        [
            V(to),
            new Vector2((float)(baseX - uy * headWidth / 2), (float)(baseY + ux * headWidth / 2)),
            new Vector2((float)(baseX + uy * headWidth / 2), (float)(baseY - ux * headWidth / 2)),
        ]);
        ds.FillGeometry(head, color);
    }

    private static void DrawCounter(CanvasDrawingSession ds, CounterShape s, AnnotationColor color)
    {
        var center = V(s.Center);
        var radius = (float)s.Radius;
        ds.FillEllipse(center, radius, radius, ToColor(color));
        var inset = radius - radius / 16;
        ds.DrawEllipse(center, inset, inset, Rgba(230, 255, 255, 255), Math.Max(1, radius / 8));
        var rect = new Rect(s.Center.X - s.Radius, s.Center.Y - s.Radius, s.Radius * 2, s.Radius * 2);
        TextRendering.DrawCentered(ds, s.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), rect, s.Radius * 1.05,
            ToColor(TextAppearance.AutomaticBackground(color)));
    }
}
