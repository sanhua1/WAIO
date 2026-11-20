using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>窗口识别(MAIO WindowPickerTests;排除的对象换成 Windows 上对应的窗口)。</summary>
public class WindowPickerTests
{
    private static WindowInfo Window(
        int id, PxRect frame, int pid = 10, string cls = "App", double alpha = 1, bool clickThrough = false, bool excluded = false) =>
        new(id, pid, frame, cls, alpha, clickThrough, excluded);

    [Fact]
    public void 排除桌面_不出现在截屏里的_透明和穿透的浮层_太小的窗口()
    {
        var screen = new PxRect(0, 0, 2560, 1440);
        WindowInfo[] windows =
        [
            Window(1, screen, cls: "Progman"),
            Window(2, screen, clickThrough: true),
            Window(3, new PxRect(0, 0, 300, 300), excluded: true),
            Window(4, new PxRect(0, 0, 4, 4)),
            Window(5, new PxRect(0, 0, 300, 300), alpha: 0),
            Window(6, new PxRect(0, 30, 2560, 1358)),
            Window(7, screen, cls: "WorkerW"),
        ];

        var candidates = WindowPicker.Candidates(windows);

        Assert.Equal([6], candidates.Select(w => (int)w.Handle));
        Assert.Null(WindowPicker.WindowAt(new PxPoint(10, 10), candidates));
        Assert.Equal(6, (int)WindowPicker.WindowAt(new PxPoint(10, 100), candidates)!.Handle);
    }

    [Fact]
    public void WAIO自己看得见的窗口_如启动台和设置窗口_可以被选中()
    {
        WindowInfo[] windows =
        [
            Window(1, new PxRect(100, 100, 50, 50), pid: 99), // 启动台面板
            Window(2, new PxRect(0, 0, 800, 600), pid: 99), // 设置窗口
            Window(3, new PxRect(0, 0, 900, 700), pid: 99, excluded: true), // 录屏控制条之类
        ];

        var candidates = WindowPicker.Candidates(windows);

        Assert.Equal([1, 2], candidates.Select(w => (int)w.Handle));
        Assert.Equal(1, (int)WindowPicker.WindowAt(new PxPoint(120, 120), candidates)!.Handle);
        Assert.Equal(2, (int)WindowPicker.WindowAt(new PxPoint(10, 10), candidates)!.Handle);
    }

    [Fact]
    public void 最上层的窗口优先()
    {
        WindowInfo[] candidates = [Window(1, new PxRect(100, 100, 100, 100)), Window(2, new PxRect(0, 0, 500, 500))];

        Assert.Equal(1, (int)WindowPicker.WindowAt(new PxPoint(150, 150), candidates)!.Handle);
        Assert.Equal(2, (int)WindowPicker.WindowAt(new PxPoint(50, 50), candidates)!.Handle);
    }
}

/// <summary>格式与布局(MAIO ScreenshotFormattingTests)。</summary>
public class ScreenshotFormattingTests
{
    [Fact]
    public void 颜色格式()
    {
        var color = new SampledColor(26, 43, 5);
        Assert.Equal("#1A2B05", ColorFormat.Hex.Format(color));
        Assert.Equal("rgb(26, 43, 5)", ColorFormat.Rgb.Format(color));
        Assert.Equal(ColorFormat.Rgb, ColorFormat.Hex.Next());
    }

    [Fact]
    public void 文件名按时间生成_重名加序号()
    {
        var date = new DateTimeOffset(2026, 11, 3, 14, 5, 33, TimeSpan.FromHours(8));
        var first = ScreenshotFileName.Make(date, _ => false);
        Assert.Equal("WAIO 2026-11-03 14.05.33.png", first);
        Assert.Equal("WAIO 2026-11-03 14.05.33 2.png", ScreenshotFileName.Make(date, n => n == first));
    }

    [Fact]
    public void 工具栏优先放下方_再上方_再选区里面()
    {
        var bounds = new PxRect(0, 0, 1000, 800);
        var below = OverlayLayout.ToolbarFrame(120, 34, new PxRect(100, 100, 300, 200), bounds, 1);
        Assert.Equal((280, 308), (below.X, below.Y));

        var above = OverlayLayout.ToolbarFrame(120, 34, new PxRect(100, 500, 300, 290), bounds, 1);
        Assert.Equal(458, above.Y);

        var inside = OverlayLayout.ToolbarFrame(120, 34, bounds, bounds, 1);
        Assert.Equal((992, 792), (inside.Right, inside.Bottom));
    }

    [Fact]
    public void 放大镜靠近屏幕边缘时换到另一侧()
    {
        var bounds = new PxRect(0, 0, 1000, 800);
        var near = OverlayLayout.MagnifierFrame(120, 174, new PxPoint(10, 10), bounds, 1);
        Assert.Equal((30, 30), (near.X, near.Y));

        var flipped = OverlayLayout.MagnifierFrame(120, 174, new PxPoint(990, 790), bounds, 1);
        Assert.Equal((990 - 20 - 120, 790 - 20 - 174), (flipped.X, flipped.Y));
    }

    [Fact]
    public void 尺寸标签放在选区左上角外侧_放不下放内侧()
    {
        var bounds = new PxRect(0, 0, 1000, 800);
        Assert.Equal(new PxRect(100, 76, 80, 20), OverlayLayout.SizeLabelFrame(80, 20, new PxRect(100, 100, 300, 200), bounds, 1));
        Assert.Equal(new PxRect(0, 4, 80, 20), OverlayLayout.SizeLabelFrame(80, 20, new PxRect(0, 0, 300, 200), bounds, 1));
    }
}

/// <summary>历史清理规则(MAIO PinAndHistoryTests 的 over limit)。</summary>
public class HistoryRetentionTests
{
    private static ScreenshotRecord Record(string id, int day, long bytes, bool pinned = false) => new()
    {
        Id = id, CreatedAt = new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), Bytes = bytes, Pinned = pinned,
    };

    [Fact]
    public void 超过上限时从最旧的删起_收藏的不删()
    {
        ScreenshotRecord[] records = [Record("new", 5, 100), Record("pinned", 1, 100, pinned: true), Record("old", 2, 100), Record("mid", 3, 100)];

        Assert.Equal(["old", "mid"], HistoryRetention.OverLimit(records, 200));
        Assert.Empty(HistoryRetention.OverLimit(records, null));
        Assert.Equal(["old"], HistoryRetention.Oldest(records, 50));
        Assert.Equal(["old", "mid"], HistoryRetention.Before(records, new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void 上限的字节数()
    {
        Assert.Equal(2_000_000_000, HistoryLimit.Gb2.Bytes());
        Assert.Null(HistoryLimit.Unlimited.Bytes());
        Assert.Equal("2 GB", HistoryLimit.Gb2.Title());
    }
}
