using System.Diagnostics;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>合成一份"长文档",从不同位置截取窗口,模拟滚动(移植 MAIO ScrollStitchTests 的 Document)。</summary>
internal sealed class Document
{
    public Document(int width, int height, ulong seed = 7)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
        var state = seed;
        // 有内容的行是随机灰度(真实截图每个格子有几十列、256 级灰度,区分度很高),段落之间有空白行
        for (var y = 0; y < height; y++)
        {
            var blank = y / 12 % 5 == 4;
            for (var x = 0; x < width; x++)
            {
                state = unchecked(state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL);
                Pixels[y * width + x] = blank ? (byte)255 : (byte)((state >> 33) % 220);
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>窗口:显示文档第 offset 行开始的内容;顶部 header 行、底部 footer 行固定;左边 sidebar 列固定。</summary>
    public GrayFrame View(int offset, int height, int header = 0, int footer = 0, int sidebar = 0)
    {
        var result = new byte[Width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                result[y * Width + x] = y < header ? (byte)(60 + (x * 7 + y * 3) % 120)
                    : y >= height - footer ? (byte)(90 + (x * 5 + y) % 100)
                    : x < sidebar ? (byte)((x * 11 + y * 13) % 200)
                    : Pixels[(offset + y) * Width + x];
            }
        }

        return new GrayFrame(Width, height, result);
    }

    /// <summary>同样的内容做成 BGRA(灰度三个通道相同)。</summary>
    public static PixelBuffer Bgra(GrayFrame frame)
    {
        var bytes = new byte[frame.Width * frame.Height * 4];
        for (var i = 0; i < frame.Pixels.Length; i++)
        {
            var v = frame.Pixels[i];
            bytes[i * 4] = v;
            bytes[i * 4 + 1] = v;
            bytes[i * 4 + 2] = v;
            bytes[i * 4 + 3] = 255;
        }

        return new PixelBuffer(frame.Width, frame.Height, bytes);
    }
}

/// <summary>长截图的位移估计(MAIO ScrollMatcherTests,同输入同期望)。</summary>
public class ScrollMatcherTests
{
    private readonly Document _document = new(200, 1200);

    private static int? OffsetOf(GrayFrame previous, GrayFrame current) =>
        ScrollMatcher.Match(previous, current) is ScrollOutcome.Moved moved ? moved.Match.Offset : null;

    [Fact]
    public void 找到滚动距离()
    {
        Assert.Equal(97, OffsetOf(_document.View(0, 300), _document.View(97, 300)));
        Assert.Equal(2, OffsetOf(_document.View(0, 300), _document.View(2, 300)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(60)]
    [InlineData(140)]
    [InlineData(205)]
    public void 小位移和大位移都精确到1像素(int shift)
    {
        Assert.Equal(shift, OffsetOf(_document.View(40, 300), _document.View(40 + shift, 300)));
    }

    [Fact]
    public void 往回滚是负位移()
    {
        Assert.Equal(-60, OffsetOf(_document.View(300, 300), _document.View(240, 300)));
    }

    [Fact]
    public void 容忍轻微的渲染噪声()
    {
        var previous = _document.View(100, 300);
        var clean = _document.View(175, 300);
        var pixels = (byte[])clean.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)Math.Min(255, pixels[i] + 6);
        }

        Assert.Equal(75, OffsetOf(previous, new GrayFrame(clean.Width, clean.Height, pixels)));
    }

    [Fact]
    public void 忽略吸顶栏_页脚和侧栏()
    {
        var first = _document.View(50, 300, header: 30, footer: 20, sidebar: 40);
        var second = _document.View(170, 300, header: 30, footer: 20, sidebar: 40);
        Assert.Equal(120, OffsetOf(first, second));
    }

    [Fact]
    public void 完全相同的帧_没动()
    {
        var frame = _document.View(10, 300);
        Assert.IsType<ScrollOutcome.Unchanged>(ScrollMatcher.Match(frame, frame));
    }

    [Fact]
    public void 只有一小块变化_当作没动()
    {
        var frame = _document.View(10, 300);
        var pixels = (byte[])frame.Pixels.Clone();
        for (var y = 100; y < 105; y++)
        {
            for (var x = 50; x < 60; x++)
            {
                pixels[y * 200 + x] ^= 0x80; // 光标闪烁这类 50 个像素的变化
            }
        }

        Assert.IsType<ScrollOutcome.Unchanged>(ScrollMatcher.Match(frame, new GrayFrame(200, 300, pixels)));
    }

    [Fact]
    public void 没有重叠时_判为对不上而不是给出错误位移()
    {
        var other = new Document(200, 1200, seed: 99);
        Assert.IsType<ScrollOutcome.Unknown>(ScrollMatcher.Match(_document.View(0, 300), other.View(0, 300)));

        // 滚动超过画面的 70%:重叠太少,不冒险
        Assert.Null(OffsetOf(_document.View(0, 300), _document.View(250, 300)));
    }

    [Fact]
    public void 重复的线条不能决定结果()
    {
        // 每一行都一样的竖线、每 20 行一条的分隔线:很多位移都"对得上",不能随便选一个
        var pixels = new byte[200 * 1200];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < 1200; y++)
        {
            pixels[y * 200 + 60] = 0;
            pixels[y * 200 + 140] = 0;
            if (y % 20 == 0)
            {
                for (var x = 30; x < 170; x++)
                {
                    pixels[y * 200 + x] = 0;
                }
            }
        }

        GrayFrame View(int top) => new(200, 300, pixels[(top * 200)..((top + 300) * 200)]);

        Assert.IsNotType<ScrollOutcome.Moved>(ScrollMatcher.Match(View(0), View(37)));
    }

    [Fact]
    public void 尺寸不同_对不上()
    {
        Assert.IsType<ScrollOutcome.Unknown>(ScrollMatcher.Match(_document.View(0, 300), _document.View(0, 299)));
    }

    [Fact]
    public void 一屏大小的帧_足够快()
    {
        // 1920×1000 的真实尺寸(宽度先抽样到 512)。只防止算法退化成逐个位移全分辨率比较,阈值很宽
        var document = new Document(1920, 2400);
        var previous = document.View(0, 1000, header: 60);
        var current = document.View(180, 1000, header: 60);
        ScrollMatcher.Match(previous, current); // 预热 JIT
        var clock = Stopwatch.StartNew();
        var outcome = ScrollMatcher.Match(previous, current);
        clock.Stop();
        Assert.Equal(180, Assert.IsType<ScrollOutcome.Moved>(outcome).Match.Offset);
        Assert.True(clock.ElapsedMilliseconds < 500, $"用时 {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Bgra转灰度并把宽度抽样到上限()
    {
        var image = PixelBuffer.Solid(1000, 3, new SampledColor(255, 0, 0));
        var gray = GrayFrame.FromBgra(image.Pixels, image.Width, image.Height, maxWidth: 512);
        Assert.Equal(512, gray.Width);
        Assert.Equal(3, gray.Height);
        Assert.All(gray.Pixels, v => Assert.Equal(76, v)); // 255 × 77 >> 8
    }
}

/// <summary>拼接计划(MAIO StitchPlanTests,同输入同期望)。</summary>
public class StitchPlanTests
{
    private static List<byte[]> Rows(StitchPlan plan, IReadOnlyList<GrayFrame> frames)
    {
        var rows = new List<byte[]>();
        foreach (var strip in plan.Strips)
        {
            var frame = frames[strip.Frame];
            for (var y = strip.Start; y < strip.End; y++)
            {
                rows.Add(frame.Pixels[(y * frame.Width)..((y + 1) * frame.Width)]);
            }
        }

        return rows;
    }

    [Fact]
    public void 往下滚写新帧靠下的一段()
    {
        var plan = new StitchPlan(300);
        plan.Begin();

        // shift 100:band = max(150, 75+100) = 175,写帧内 125..300
        Assert.Equal((125, 300), plan.Append(1, 100));
        Assert.Equal([new StitchPlan.Strip(0, 0, 225, 0), new StitchPlan.Strip(1, 125, 300, 225)], plan.Strips);
        Assert.Equal(400, plan.CanvasHeight);
        Assert.Equal(100, plan.Position);

        // shift 20:band = 150,写帧内 150..300
        Assert.Equal((150, 300), plan.Append(2, 20));
        Assert.Equal(420, plan.CanvasHeight);
        Assert.Equal(120, plan.Position);
    }

    [Fact]
    public void 在画布范围内往回滚_只移动位置()
    {
        var plan = new StitchPlan(300);
        plan.Begin();
        plan.Append(1, 100);
        Assert.Null(plan.Append(2, -80));
        Assert.Equal(20, plan.Position);
        Assert.Equal(400, plan.CanvasHeight);
        Assert.Equal(2, plan.Strips.Count);
    }

    [Fact]
    public void 往回滚过起点_画布向上扩展且没有空洞()
    {
        var plan = new StitchPlan(300);
        plan.Begin();
        plan.Append(1, 100);
        Assert.Equal((0, 150), plan.Append(2, -130));
        Assert.Equal(0, plan.Position);
        Assert.Equal(430, plan.CanvasHeight);
        Assert.Equal(new StitchPlan.Strip(2, 0, 150, 0), plan.Strips[0]);

        var covered = 0;
        foreach (var strip in plan.Strips)
        {
            Assert.Equal(covered, strip.Top);
            covered += strip.Height;
        }

        Assert.Equal(plan.CanvasHeight, covered);
    }

    [Fact]
    public void 匹配加拼接_还原带吸顶栏和页脚的文档()
    {
        var document = new Document(160, 1500);
        int[] offsets = [0, 110, 230, 300, 420, 560, 600, 603, 700];
        var frames = offsets.Select(o => document.View(o, 320, header: 24, footer: 16)).ToList();
        var plan = new StitchPlan(320);
        plan.Begin();
        var position = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            var moved = Assert.IsType<ScrollOutcome.Moved>(ScrollMatcher.Match(frames[i - 1], frames[i]));
            Assert.Equal(offsets[i] - offsets[i - 1], moved.Match.Offset);
            plan.Append(i, moved.Match.Offset);
            position += moved.Match.Offset;
            Assert.Equal(position, plan.Position);
        }

        // 吸顶栏只出现一次,正文是文档的连续内容,页脚只在最后
        var rows = Rows(plan, frames);
        const int Last = 700;
        var content = Enumerable.Range(24, Last + 320 - 16 - 24).SelectMany(y => document.Pixels[(y * 160)..((y + 1) * 160)]).ToArray();
        Assert.Equal(24 + content.Length / 160 + 16, rows.Count);
        Assert.Equal(content, rows.Skip(24).Take(content.Length / 160).SelectMany(r => r).ToArray());
    }

    [Fact]
    public void 往回滚再往下滚_文档仍然连续()
    {
        var document = new Document(160, 1500);
        var frames = new[] { 0, 120, 240, 180, 300, 420 }.Select(o => document.View(o, 300)).ToList();
        var plan = new StitchPlan(300);
        plan.Begin();
        for (var i = 1; i < frames.Count; i++)
        {
            var moved = Assert.IsType<ScrollOutcome.Moved>(ScrollMatcher.Match(frames[i - 1], frames[i]));
            plan.Append(i, moved.Match.Offset);
        }

        Assert.Equal(420, plan.Position);
        Assert.Equal(720, plan.CanvasHeight);
        Assert.Equal(document.Pixels[..(720 * 160)], Rows(plan, frames).SelectMany(r => r).ToArray());
    }
}

/// <summary>长图合成(MAIO LongImageBuilderTests)。</summary>
public class LongImageBuilderTests
{
    [Fact]
    public void 按顺序合成各段()
    {
        var document = new Document(120, 900);
        int[] offsets = [0, 90, 200, 260, 262];
        var frames = offsets.Select(o => document.View(o, 240, header: 20, footer: 12)).ToList();
        var builder = new LongImageBuilder(Document.Bgra(frames[0]));
        for (var i = 1; i < frames.Count; i++)
        {
            builder.Append(Document.Bgra(frames[i]), offsets[i] - offsets[i - 1]);
        }

        var image = builder.Compose();
        var gray = GrayFrame.FromBgra(image.Pixels, image.Width, image.Height);
        const int Last = 262;
        Assert.Equal(20 + (Last + 240 - 12 - 20) + 12, gray.Height);
        Assert.Equal(frames[0].Pixels[..(120 * 20)], gray.Pixels[..(120 * 20)]);
        Assert.Equal(document.Pixels[(20 * 120)..((Last + 240 - 12) * 120)], gray.Pixels[(20 * 120)..((gray.Height - 12) * 120)]);
        Assert.Equal(frames[^1].Pixels[((240 - 12) * 120)..], gray.Pixels[((gray.Height - 12) * 120)..]);
    }

    [Fact]
    public void 预览保持长图的宽高比()
    {
        var frame = PixelBuffer.Solid(400, 300, new SampledColor(10, 20, 30));
        var builder = new LongImageBuilder(frame, previewWidth: 100);
        builder.Append(frame, 150);
        var preview = builder.Preview();
        Assert.Equal(450, builder.Height);
        Assert.Equal(100, preview.Width);
        Assert.Equal(113, preview.Height); // 450 × 0.25 四舍五入
        Assert.Equal(new SampledColor(10, 20, 30), preview.ColorAt(50, 112));
    }

    [Fact]
    public void 落在已拼范围里_画布不变()
    {
        var frame = PixelBuffer.Solid(40, 30, new SampledColor(1, 2, 3));
        var builder = new LongImageBuilder(frame);
        Assert.True(builder.Append(frame, 10));
        Assert.False(builder.Append(frame, -5));
        Assert.Equal(40, builder.Height);
        Assert.Equal(5, builder.Plan.Position);
    }
}

/// <summary>长截图的边框、右侧面板、预览绿框位置(MAIO ScrollCaptureFrame.sidebarFrame)。</summary>
public class ScrollCaptureLayoutTests
{
    private static readonly PxRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void 面板放在选区右边_顶部对齐_延伸到工作区底部()
    {
        var area = ScrollCaptureLayout.Sidebar(new PxRect(100, 200, 800, 600), Work, 180, 10, 160);
        Assert.Equal(new PxRect(910, 200, 180, 1040 - 10 - 200), area);
    }

    [Fact]
    public void 右边放不下放左边_都放不下放在选区里靠右()
    {
        Assert.Equal(1000 - 10 - 180, ScrollCaptureLayout.Sidebar(new PxRect(1000, 100, 900, 500), Work, 180, 10, 160).X);
        Assert.Equal(1920 - 180 - 10, ScrollCaptureLayout.Sidebar(new PxRect(0, 0, 1920, 1040), Work, 180, 10, 160).X);
    }

    [Fact]
    public void 选区很靠下时_面板保持最小高度并往上挪()
    {
        var area = ScrollCaptureLayout.Sidebar(new PxRect(100, 950, 400, 80), Work, 180, 10, 160);
        Assert.Equal(160, area.Height);
        Assert.True(area.Bottom <= Work.Bottom);
    }

    [Fact]
    public void 边框是选区外的四条_不盖住选区()
    {
        var selection = new PxRect(100, 100, 50, 40);
        var bars = ScrollCaptureLayout.Border(selection, 2);
        Assert.Equal(4, bars.Count);
        Assert.All(bars, b => Assert.False(b.Intersects(selection)));
        Assert.Equal(new PxRect(98, 98, 54, 2), bars[0]);
        Assert.Equal(new PxRect(98, 140, 54, 2), bars[1]);
        Assert.Equal(new PxRect(98, 100, 2, 40), bars[2]);
        Assert.Equal(new PxRect(150, 100, 2, 40), bars[3]);
    }

    [Fact]
    public void 绿框按比例标出当前画面且不超出预览()
    {
        Assert.Equal((50.0, 25.0), ScrollCaptureLayout.ViewportMarker(4000, 1000, 2000, 100));
        Assert.Equal((0.0, 100.0), ScrollCaptureLayout.ViewportMarker(1000, 1000, 0, 100));
        var (top, height) = ScrollCaptureLayout.ViewportMarker(4000, 1000, 3900, 100);
        Assert.Equal(100, top + height);
    }
}
