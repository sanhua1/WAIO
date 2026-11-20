using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Waio.Feature.Screenshot.UI;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>贴图状态、历史搜索、重新编辑的位置换算(MAIO PinStateTests / HistorySearchTests / editContext 的测试)。</summary>
public sealed class PinTests
{
    private static PinState State(int width = 200, int height = 100) => new() { ImageSize = (width, height), Scale = 1.5 };

    [Fact]
    public void 内容大小随旋转交换宽高_窗口四周留外边距()
    {
        var state = State();
        Assert.Equal((200, 100), state.ContentSize);
        Assert.Equal(21, state.Margin); // 14 pt × 1.5
        Assert.Equal((242, 142), state.WindowSize);

        var turned = state.Rotated(clockwise: true);
        Assert.Equal(1, turned.QuarterTurns);
        Assert.Equal((100, 200), turned.ContentSize);
        Assert.Equal(3, state.Rotated(clockwise: false).QuarterTurns);
        Assert.Equal(0, turned.Rotated(true).Rotated(true).Rotated(true).QuarterTurns);
    }

    [Fact]
    public void 以鼠标为中心缩放_鼠标下的点不动()
    {
        var (zoomed, origin) = State().ZoomedTo(2, (0.25, 0.5), new PxPoint(100, 100));
        Assert.Equal((400, 200), zoomed.ContentSize);
        // 鼠标在 (150, 150):缩放前是内容的 (25%, 50%),缩放后仍然在 (25%, 50%)
        Assert.Equal(new PxPoint(50, 50), origin);
        Assert.Equal(150, origin.X + 0.25 * 400);
    }

    [Fact]
    public void 按步缩放每步5个百分点并对齐_范围10到500()
    {
        var state = State();
        Assert.Equal(1.05, state.ZoomedBy(1, (0.5, 0.5), default).State.Zoom, 6);
        Assert.Equal(1.1, (state with { Zoom = 1.03 }).ZoomedBy(1, (0.5, 0.5), default).State.Zoom, 6);
        Assert.Equal(0.1, (state with { Zoom = 0.12 }).ZoomedBy(-5, (0.5, 0.5), default).State.Zoom, 6);
        Assert.Equal(5, state.ZoomedTo(9, (0.5, 0.5), default).State.Zoom, 6);
    }

    [Fact]
    public void 大图的放大上限_不超过8192像素和4000万像素()
    {
        Assert.Equal(5, State().ZoomLimit);
        Assert.Equal(8192.0 / 4000, State(4000, 1000).ZoomLimit, 6);
        Assert.Equal(8192.0 / 3840, State(3840, 2160).ZoomLimit, 6); // 最长边先到上限
        Assert.Equal(Math.Sqrt(40_000_000.0 / (6000.0 * 6000)), State(6000, 6000).ZoomLimit, 6); // 总像素先到上限
        Assert.True(State(3840, 2160).ZoomedTo(5, (0.5, 0.5), default).State.ContentSize.Width <= 8192);
    }

    [Fact]
    public void 透明度10到100_数字键1到9是10到90_0是100()
    {
        var state = State();
        Assert.Equal(0.1, state.WithOpacity(0).Opacity, 6);
        Assert.Equal(1, state.WithOpacity(2).Opacity, 6);
        Assert.Equal(0.95, state.OpacityStepped(-1).Opacity, 6);
        Assert.Equal(0.3, PinState.OpacityForDigit(3));
        Assert.Equal(1, PinState.OpacityForDigit(0));
        Assert.Null(PinState.OpacityForDigit(10));
    }

    [Fact]
    public void 没有原始位置时放在显示器中间_太大时缩小到80()
    {
        var display = new PxRect(1920, 0, 1000, 800);
        var (origin, zoom) = PinState.Centered((200, 100), display);
        Assert.Equal(1, zoom);
        Assert.Equal(new PxPoint(1920 + 400, 350), origin);

        var (bigOrigin, bigZoom) = PinState.Centered((2000, 400), display);
        Assert.Equal(0.4, bigZoom, 6);
        Assert.Equal(new PxPoint(1920 + 100, 320), bigOrigin);
    }

    [Theory]
    [InlineData(false, false, false, PinModifiers.Target.Zoom)]
    [InlineData(true, false, false, PinModifiers.Target.Opacity)]
    [InlineData(false, true, false, null)]
    [InlineData(true, true, false, null)]
    public void 修饰键决定滚轮调什么_默认直接滚轮缩放_Ctrl调透明度(bool ctrl, bool alt, bool shift, PinModifiers.Target? expected)
    {
        var controls = new PinControls();
        Assert.Equal(expected, PinModifiers.TargetFor(ctrl, alt, shift, controls.Zoom, controls.Opacity));
    }

    [Fact]
    public void 两个修饰键相同时缩放优先()
    {
        Assert.Equal(PinModifiers.Target.Zoom, PinModifiers.TargetFor(false, true, false, PinModifier.Alt, PinModifier.Alt));
    }

    [Fact]
    public void 历史搜索按日期来源程序识别文字_多个词都要命中_不区分大小写()
    {
        var day = new DateTimeOffset(2026, 11, 3, 14, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 3)));
        ScreenshotRecord[] records =
        [
            new() { Id = "a", CreatedAt = day, SourceApp = "WeChat", OcrText = "会议纪要" },
            new() { Id = "b", CreatedAt = day.AddDays(1), SourceApp = "chrome" },
        ];
        Assert.Equal(["a"], HistorySearch.Filter(records, "2026-11-03").Select(r => r.Id));
        Assert.Equal(["a"], HistorySearch.Filter(records, "wechat 会议").Select(r => r.Id));
        Assert.Empty(HistorySearch.Filter(records, "wechat chrome"));
        Assert.Equal(2, HistorySearch.Filter(records, "  ").Count);
    }

    [Fact]
    public void 历史按天分组_新的在前()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        ScreenshotRecord[] records =
        [
            new() { Id = "old", CreatedAt = now.AddDays(-2) },
            new() { Id = "new", CreatedAt = now },
            new() { Id = "newer", CreatedAt = now.AddMinutes(1) },
        ];
        var groups = HistorySearch.GroupByDay(records);
        Assert.Equal(2, groups.Count);
        Assert.Equal(["newer", "new"], groups[0].Records.Select(r => r.Id));
        Assert.Equal(["old"], groups[1].Records.Select(r => r.Id));
    }

    [Fact]
    public void 重新编辑_放得下按1比1以贴图中心为中心_不超出显示器()
    {
        var display = new PxRect(0, 0, 1920, 1080);
        var centered = EditPlacement.For((400, 200), display, null);
        Assert.Equal(new EditPlacement(new PxRect(760, 440, 400, 200), 1), centered);

        var nearEdge = EditPlacement.For((400, 200), display, new PxPoint(1900, 10));
        Assert.Equal(new PxRect(1520, 0, 400, 200), nearEdge.Rect);
    }

    [Fact]
    public void 重新编辑_放不下时缩小到显示器的90()
    {
        var placement = EditPlacement.For((1000, 4000), new PxRect(0, 0, 1920, 1000), null);
        Assert.Equal(4000 / 900.0, placement.Zoom, 6);
        Assert.Equal(900, placement.Rect.Height);
        Assert.Equal(225, placement.Rect.Width);
    }

    [Fact]
    public void 重新编辑的标注坐标_换算到屏幕再换算回来不变()
    {
        var original = PixelBuffer.Solid(1000, 4000, new SampledColor(255, 255, 255));
        var display = new DisplayInfo(0, new PxRect(0, 0, 1920, 1000), 1);
        var frozen = new FrozenDisplay(display, PixelBuffer.Solid(4, 4, new SampledColor(0, 0, 0)));
        var stored = new Annotation(new RectangleShape(new RectD(100, 200, 300, 400), false, false), AnnotationColors.Presets[0], 8);

        var edit = ScreenshotController.EditContextFor(original, 2, [stored], null, [frozen])!;
        using var source = edit.Source;
        var zoom = source.Zoom;
        Assert.Equal(4000 / 900.0, zoom, 6);
        Assert.Equal(1.0 / zoom * 2, source.Scale, 6); // 文字内边距等按屏幕上的大小换算
        var onScreen = edit.Annotations[0];
        Assert.Equal(8 / zoom, onScreen.LineWidth, 6);

        var back = onScreen.Transformed(new VectorD(-source.Origin.X, -source.Origin.Y), zoom);
        var frame = ((RectangleShape)back.Shape).Frame;
        Assert.Equal(100, frame.X, 6);
        Assert.Equal(200, frame.Y, 6);
        Assert.Equal(300, frame.Width, 6);
        Assert.Equal(8, back.LineWidth, 6);
    }

    [Fact]
    public void 缩小显示的原图_屏幕矩形和图片像素互相换算()
    {
        using var source = new EffectSource(PixelBuffer.Solid(400, 200, new SampledColor(1, 2, 3)), new PxPoint(100, 50), 1, zoom: 2);
        Assert.Equal(new RectD(100, 50, 200, 100), source.Bounds);
        Assert.Equal(new PxRect(20, 40, 60, 40), source.PixelRect(new RectD(110, 70, 30, 20)));
        Assert.Equal(new RectD(110, 70, 30, 20), source.GlobalRect(new PxRect(20, 40, 60, 40)));
    }
}
