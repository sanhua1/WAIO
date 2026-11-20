using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>
/// 渲染(MAIO AnnotationRenderTests):输出和屏幕显示用同一套代码,这里检查方向、位置和像素换算。用 GPU(Win2D),不显示任何窗口。
/// 设置环境变量 WAIO_RENDER_SAMPLE=路径 时,把包含全部工具的样图写到这个路径,方便人工查看。
/// </summary>
public class AnnotationRenderTests
{
    private static readonly SampledColor White = new(255, 255, 255);

    /// <summary>800×600 像素、缩放 2 的"屏幕"(左上角在全局 (2000, 0)):白底,左上角 80×80 灰块,下面几行黑色条纹当作文字。</summary>
    private static EffectSource MakeSource()
    {
        var image = PixelBuffer.Solid(800, 600, White);
        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                image.SetColor(x, y, new SampledColor(128, 128, 128));
            }
        }

        for (var row = 0; row < 6; row++)
        {
            for (var y = 120 + row * 80; y < 140 + row * 80; y++)
            {
                for (var x = 40; x < 760; x += 3)
                {
                    image.SetColor(x, y, new SampledColor(0, 0, 0));
                }
            }
        }

        return new EffectSource(image, new PxPoint(2000, 0), 2);
    }

    [Fact]
    public void 输出保持方向和像素位置()
    {
        using var source = MakeSource();
        var filled = new Annotation(new RectangleShape(new RectD(2100, 20, 40, 20), true, false), AnnotationColors.Blue, 4);

        var output = AnnotationRenderer.RenderOutput([filled], new PxRect(2000, 0, 200, 100), source);

        Assert.Equal((200, 100), (output.Width, output.Height));
        Assert.Equal(new SampledColor(128, 128, 128), output.ColorAt(5, 5)); // 左上角的灰块还在左上角
        Assert.Equal(new SampledColor(0, 122, 255), output.ColorAt(120, 30)); // 蓝色矩形:(100, 20)~(140, 40)
        Assert.Equal(White, output.ColorAt(120, 50));
    }

    [Fact]
    public void 马赛克看不出原来的像素()
    {
        using var source = MakeSource();
        var mosaic = new Annotation(new MosaicShape(new RectD(2000, 0, 80, 80), 40), AnnotationColors.Red, 0);
        var output = AnnotationRenderer.RenderOutput([mosaic], new PxRect(2000, 0, 800, 600), source);

        // 灰块是均匀的,块内处处相同;和右边白底不在同一块
        Assert.Equal(output.ColorAt(2, 2), output.ColorAt(38, 38));
        Assert.Equal(White, output.ColorAt(90, 10));
    }

    [Fact]
    public void 马赛克每块取平均色()
    {
        var image = PixelBuffer.Solid(4, 2, White);
        image.SetColor(0, 0, new SampledColor(0, 0, 0));
        image.SetColor(1, 1, new SampledColor(0, 0, 0));

        var small = EffectCache.Average(image, new PxRect(0, 0, 4, 2), 2, 1);

        Assert.Equal([127, 127, 127, 255, 255, 255, 255, 255], small);
    }

    [Fact]
    public async Task 每种工具都能画()
    {
        using var source = MakeSource();
        var output = AnnotationRenderer.RenderOutput(SampleItems(2000, 0, 2), new PxRect(2000, 0, 800, 600), source);

        Assert.Equal(800, output.Width);
        Assert.NotEqual(White, output.ColorAt(20, 60)); // 矩形左边
        Assert.NotEqual(White, output.ColorAt(60, 460)); // 序号(中心是白色数字,取圆里偏左的位置)
        if (Environment.GetEnvironmentVariable("WAIO_RENDER_SAMPLE") is { Length: > 0 } path)
        {
            await File.WriteAllBytesAsync(path, await ImageCodec.EncodePngAsync(output, 2));
        }
    }

    [Fact]
    public void 文字框大小_含内边距_随字号变化()
    {
        var small = TextRendering.Size(new TextBox(new PointD(0, 0), "标注 Text", 18, false, TextDecoration.Plain), 1);
        var large = TextRendering.Size(new TextBox(new PointD(0, 0), "标注 Text", 36, true, TextDecoration.Plain), 1);

        Assert.True(small.Width > 12 && small.Height > 6);
        Assert.True(large.Width > small.Width * 1.6 && large.Height > small.Height * 1.5);
    }

    /// <summary>全部工具的样图(MAIO sampleItems,坐标按 pt 写,乘缩放比例)。</summary>
    internal static Annotation[] SampleItems(double ox, double oy, double s)
    {
        PointD P(double x, double y) => new(ox + x * s, oy + y * s);
        RectD R(double x, double y, double w, double h) => new(ox + x * s, oy + y * s, w * s, h * s);
        TextBox Measured(TextBox box)
        {
            var (w, h) = TextRendering.Size(box, s);
            return box with { Width = w, Height = h };
        }

        var text = Measured(new TextBox(P(220, 200), "文字 Text", 18 * s, true, TextDecoration.Background) { BackgroundColor = AnnotationColors.Yellow });
        var outline = Measured(new TextBox(P(220, 240), "描边文字", 20 * s, false, TextDecoration.Outline) { BackgroundColor = AnnotationColors.Black });
        return
        [
            new(new RectangleShape(R(10, 10, 80, 50), false, true), AnnotationColors.Red, 4 * s),
            new(new EllipseShape(R(100, 10, 80, 50), false), AnnotationColors.Green, 4 * s),
            new(new ArrowShape(P(200, 60), P(280, 15)), AnnotationColors.Red, 4 * s),
            new(new LineShape(P(290, 15), P(390, 60), true), AnnotationColors.Blue, 3 * s),
            new(new PenShape([P(10, 120), P(30, 100), P(50, 130), P(70, 95), P(90, 125)]), AnnotationColors.Purple, 4 * s),
            new(new HighlighterShape([P(10, 160), P(380, 160)]), AnnotationColors.Yellow, 16 * s),
            new(new MosaicShape(R(10, 255, 150, 40), 8 * s), AnnotationColors.Red, 0),
            new(new BlurShape(R(200, 160, 150, 40), 6 * s), AnnotationColors.Red, 0),
            new(new CounterShape(P(40, 230), 1, 13 * s), AnnotationColors.Red, 0),
            new(new CounterShape(P(90, 230), 12, 13 * s), AnnotationColors.Yellow, 0),
            new(new TextShape(text), AnnotationColors.Blue, 0),
            new(new TextShape(outline), AnnotationColors.Red, 0),
        ];
    }
}
