using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>贴图上选择识别出的文字:逐字位置、坐标换算(含旋转)、选择模型。</summary>
public sealed class PinTextTests
{
    /// <summary>一段文字:每个字宽 10,从 <paramref name="x"/> 开始。</summary>
    private static TextSegment Segment(string text, double x, double y, double height = 20) =>
        new(new TextBounds(x, y, text.Length * 10, height), [.. text.Select((c, i) => new TextChar(c.ToString(), x + i * 10, x + i * 10 + 10))]);

    [Fact]
    public void 逐字位置_按识别步数换算到文字框_首尾外推不超出框()
    {
        // 输入宽 400(有内容的 200,右侧补边),100 步 → 每步 4 个输入像素;文字框宽 100 → 输入 1 像素 = 框 0.5 像素
        var box = new TextBounds(50, 0, 100, 20);
        var spans = OcrPostProcess.CharacterSpans([("a", 10), ("b", 20), ("c", 30)], 100, 400, 200, box);

        // 中心:50 + (t + 0.5) × 4 × 0.5 → 71、91、111
        Assert.Equal([61.0, 81, 101], spans.Select(s => s.Left));
        Assert.Equal([81.0, 101, 121], spans.Select(s => s.Right));
        Assert.Equal((box.X, box.Right), (OcrPostProcess.CharacterSpans([("a", 0)], 100, 400, 200, box)[0].Left, OcrPostProcess.CharacterSpans([("a", 0)], 100, 400, 200, box)[0].Right));
    }

    [Theory]
    [InlineData(0, 0.0, 0.0)]
    [InlineData(1, 100.0, 0.0)] // 顺时针转 90°:原图左上角转到内容右上角
    [InlineData(2, 100.0, 200.0)]
    [InlineData(3, 0.0, 200.0)]
    public void 内容坐标换回原图坐标_缩放和四种旋转(int turns, double x, double y)
    {
        // 原图 100×50,放大 2 倍;转 90° / 270° 时内容是 100×200,否则 200×100
        var state = new PinState { ImageSize = (100, 50), Zoom = 2, QuarterTurns = turns };
        var (cw, ch) = state.ContentSize;
        var (ix, iy) = state.ImagePoint(turns == 0 ? 0 : turns == 2 ? cw : x, turns == 0 ? 0 : turns == 2 ? ch : y);
        Assert.Equal(0, ix, 6);
        Assert.Equal(0, iy, 6);
    }

    [Fact]
    public void 段按阅读顺序排列_同一行用空格_换行用换行()
    {
        var layout = new PinTextLayout([Segment("右栏", 200, 0), Segment("第二行", 0, 40), Segment("左栏", 0, 2)]);
        Assert.Equal(["左栏", "右栏", "第二行"], layout.Segments.Select(s => s.Text));
        Assert.Equal("左栏 右栏\n第二行", layout.AllText);
    }

    [Fact]
    public void 插入点_按字的中点决定_点在文字外面时找最近的段()
    {
        var layout = new PinTextLayout([Segment("abcd", 0, 0), Segment("efgh", 0, 40)]);
        Assert.Equal(new TextPosition(0, 0), layout.PositionAt(4, 10)); // a 的左半边
        Assert.Equal(new TextPosition(0, 1), layout.PositionAt(6, 10)); // a 的右半边
        Assert.Equal(new TextPosition(0, 4), layout.PositionAt(500, 10)); // 行尾右边很远
        Assert.Equal(new TextPosition(1, 2), layout.PositionAt(21, 100)); // 最后一行下面
        Assert.Equal(new TextPosition(0, 2), layout.PositionAt(21, 26)); // 两行之间偏上
    }

    [Fact]
    public void 选中的文字和高亮_跨段_两端顺序无所谓()
    {
        var layout = new PinTextLayout([Segment("abcd", 0, 0), Segment("efgh", 0, 40)]);
        var (from, to) = (new TextPosition(0, 2), new TextPosition(1, 1));
        Assert.Equal("cd\ne", layout.Text(to, from));
        Assert.Equal([new TextBounds(20, 0, 20, 20), new TextBounds(0, 40, 10, 20)], layout.Highlights(from, to));
        Assert.Empty(layout.Highlights(from, from));
        Assert.Equal((new TextPosition(1, 0), new TextPosition(1, 4)), layout.SegmentRange(1));
    }

    [Fact]
    public void 鼠标在不在文字上_文字框外放宽2像素()
    {
        var layout = new PinTextLayout([Segment("abcd", 10, 10)]);
        Assert.True(layout.Contains(9, 10));
        Assert.False(layout.Contains(5, 10));
        Assert.False(layout.Contains(30, 45));
    }

    /// <summary>
    /// 真正跑 PP-OCR(离线,不碰桌面):逐字位置和墨迹位置对得上——第一个字的左边、最后一个字的右边离墨迹的边缘都不超过一个字宽。
    /// </summary>
    [Fact]
    public void PP_OCR逐字位置_和渲染出来的文字对得上()
    {
        Assert.True(PaddleOcr.ModelsPresent, "缺少识别模型,先运行 scripts/fetch-ocr-models.ps1");
        var image = ClipboardImage.RenderText("贴图选择文字 Ctrl+C", 1.5);

        var layout = TextRecognizer.RecognizeSelectable(image)!;

        var segment = Assert.Single(layout.Segments);
        Assert.Equal("贴图选择文字Ctrl+C", segment.Text.Replace(" ", "", StringComparison.Ordinal));
        var (inkLeft, inkRight) = InkRange(image);
        var charWidth = (inkRight - inkLeft) / segment.Text.Length;
        Assert.InRange(segment.Chars[0].Left, inkLeft - charWidth, inkLeft + charWidth);
        Assert.InRange(segment.Chars[^1].Right, inkRight - charWidth, inkRight + charWidth);
        Assert.True(segment.Chars.Zip(segment.Chars.Skip(1)).All(p => p.First.Right <= p.Second.Left + 0.001), "逐字范围要从左到右、不重叠");
    }

    /// <summary>深色像素(文字墨迹)的左右边界。</summary>
    private static (double Left, double Right) InkRange(PixelBuffer image)
    {
        int left = image.Width, right = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.ColorAt(x, y) is { Red: < 128, Green: < 128, Blue: < 128 })
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x + 1);
                }
            }
        }

        return (left, right);
    }
}
