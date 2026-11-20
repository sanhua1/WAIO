using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Waio.Feature.Screenshot.Domain.Annotation;
using Windows.Foundation;

namespace Waio.Feature.Screenshot.Infrastructure.Rendering;

/// <summary>
/// 文字标注的排版和绘制(MAIO TextRendering)。测量和绘制用同一套属性,包围盒准确。单位是物理像素;内边距 6×3 pt 乘缩放比例。
/// 字体用"微软雅黑 UI":中英文都有,编辑文字时的输入框也用它,两边对得上。
/// </summary>
internal static class TextRendering
{
    public const string FontFamily = "Microsoft YaHei UI";
    public const double PaddingX = 6;
    public const double PaddingY = 3;

    public static CanvasTextFormat Format(double fontSize, bool bold) => new()
    {
        FontFamily = FontFamily,
        FontSize = (float)Math.Max(1, fontSize),
        FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(bold ? 700 : 400) }, // 不用 WinUI 的 FontWeights(要 WinUI 运行时)
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    /// <summary>文字框的大小(含内边距,像素)。</summary>
    public static (double Width, double Height) Size(TextBox box, double scale)
    {
        using var layout = Layout(CanvasDevice.GetSharedDevice(), box);
        var bounds = layout.LayoutBounds;
        return (Math.Ceiling(bounds.Width) + PaddingX * scale * 2, Math.Ceiling(bounds.Height) + PaddingY * scale * 2);
    }

    /// <summary>按当前样式画文字(总是重新测量,字号、粗体改了之后底色块大小也跟着变)。</summary>
    public static void Draw(CanvasDrawingSession ds, TextBox box, AnnotationColor color, double scale, bool decorationOnly = false)
    {
        using var layout = Layout(ds.Device, box);
        var (width, height) = Size(layout, scale);
        var frame = new Rect(box.Origin.X, box.Origin.Y, width, height);
        var origin = new Vector2((float)(box.Origin.X + PaddingX * scale), (float)(box.Origin.Y + PaddingY * scale));
        var fill = AnnotationRenderer.ToColor(color);
        switch (box.Decoration)
        {
            case TextDecoration.Outline:
                // 描边:文字轮廓画一圈粗线(MAIO strokeWidth 12 = 字号的 12%),再把文字盖上去
                using (var geometry = CanvasGeometry.CreateText(layout))
                using (var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round })
                {
                    var stroke = AnnotationRenderer.ToColor(box.BackgroundColor ?? TextAppearance.AutomaticBackground(color));
                    ds.DrawGeometry(geometry, origin, stroke, (float)(box.FontSize * 0.12), style);
                }

                break;
            case TextDecoration.Background:
                // 旧数据没有背景色:色块用文字颜色,文字自动取黑或白
                var block = box.BackgroundColor ?? color;
                ds.FillRoundedRectangle(frame, (float)(4 * scale), (float)(4 * scale), AnnotationRenderer.ToColor(block));
                if (box.BackgroundColor is null)
                {
                    fill = AnnotationRenderer.ToColor(TextAppearance.AutomaticBackground(color));
                }

                break;
        }

        if (!decorationOnly)
        {
            ds.DrawTextLayout(layout, origin, fill);
        }
    }

    /// <summary>在矩形中间画一段粗体文字(序号)。</summary>
    public static void DrawCentered(CanvasDrawingSession ds, string text, Rect rect, double fontSize, Windows.UI.Color color)
    {
        using var format = Format(fontSize, bold: true);
        format.HorizontalAlignment = CanvasHorizontalAlignment.Center;
        format.VerticalAlignment = CanvasVerticalAlignment.Center;
        ds.DrawText(text, rect, color, format);
    }

    private static CanvasTextLayout Layout(ICanvasResourceCreator device, TextBox box)
    {
        using var format = Format(box.FontSize, box.Bold);
        return new CanvasTextLayout(device, box.Text.Length == 0 ? " " : box.Text, format, 100_000, 100_000);
    }

    private static (double Width, double Height) Size(CanvasTextLayout layout, double scale) =>
        (Math.Ceiling(layout.LayoutBounds.Width) + PaddingX * scale * 2, Math.Ceiling(layout.LayoutBounds.Height) + PaddingY * scale * 2);
}
