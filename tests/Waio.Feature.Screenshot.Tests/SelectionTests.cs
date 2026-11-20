using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>选区几何(MAIO SelectionGeometryTests,同样的输入和期望;坐标是物理像素,缩放比例 1)。</summary>
public class SelectionGeometryTests
{
    private static readonly PxRect Rect = new(100, 100, 200, 100);
    private const int Tolerance = 7;

    [Fact]
    public void 容差内命中手柄()
    {
        Assert.Equal(SelectionHandle.TopLeft, SelectionGeometry.Handle(new PxPoint(103, 98), Rect, Tolerance));
        Assert.Equal(SelectionHandle.Bottom, SelectionGeometry.Handle(new PxPoint(200, 200), Rect, Tolerance));
        Assert.Null(SelectionGeometry.Handle(new PxPoint(150, 150), Rect, Tolerance));
    }

    [Fact]
    public void 小选区只有四个角的手柄()
    {
        var small = new PxRect(0, 0, 10, 10);
        Assert.Equal(SelectionHandle.TopLeft, SelectionGeometry.Handle(new PxPoint(5, 0), small, Tolerance));
    }

    [Fact]
    public void 拖过对边时翻转()
    {
        Assert.Equal(new PxRect(300, 100, 50, 100), SelectionGeometry.Resize(Rect, SelectionHandle.Left, new PxPoint(350, 0)));
        Assert.Equal(new PxRect(100, 100, 220, 150), SelectionGeometry.Resize(Rect, SelectionHandle.BottomRight, new PxPoint(320, 250)));
    }

    [Fact]
    public void 移动时整体留在范围内()
    {
        var bounds = new PxRect(0, 0, 400, 300);
        Assert.Equal(new PxRect(200, 0, 200, 100), SelectionGeometry.Move(Rect, 500, -500, bounds));
    }

    [Fact]
    public void 像素矩形相对于显示器左上角()
    {
        var display = new PxRect(-1440, 0, 1440, 900);
        Assert.Equal(new PxRect(100, 10, 100, 50), SelectionGeometry.PixelRect(new PxRect(-1340, 10, 100, 50), display));
    }

    [Fact]
    public void 手柄容差按缩放比例换算()
    {
        var interaction = new CaptureInteraction();
        interaction.Select(Rect, new PxRect(0, 0, 1000, 800), scale: 1.5);
        Assert.Equal(11, interaction.HandleTolerance); // 7 pt × 1.5 ≈ 11 像素
    }
}

/// <summary>截图的鼠标交互(MAIO CaptureInteractionTests)。</summary>
public class CaptureInteractionTests
{
    private static readonly PxRect Screen = new(0, 0, 1000, 800);

    [Fact]
    public void 拖动框选_限制在屏幕内()
    {
        var interaction = new CaptureInteraction();
        interaction.MouseDown(new PxPoint(100, 100), 1, Screen, 1);
        interaction.MouseDragged(new PxPoint(1200, 300));
        interaction.MouseUp(null);

        Assert.Equal(new PxRect(100, 100, 900, 200), interaction.Selection);
        Assert.False(interaction.IsDragging);
    }

    [Fact]
    public void 单击选中高亮的窗口()
    {
        var interaction = new CaptureInteraction();
        interaction.MouseDown(new PxPoint(100, 100), 1, Screen, 1);
        interaction.MouseDragged(new PxPoint(101, 101));
        interaction.MouseUp(new PxRect(50, -20, 300, 200));

        Assert.Equal(new PxRect(50, 0, 300, 180), interaction.Selection);
    }

    [Fact]
    public void 拖动阈值按缩放比例换算()
    {
        var interaction = new CaptureInteraction();
        interaction.MouseDown(new PxPoint(100, 100), 1, Screen, 2);
        interaction.MouseDragged(new PxPoint(104, 100)); // 4 像素 < 3 pt × 2
        interaction.MouseUp(null);

        Assert.Null(interaction.Selection);
    }

    [Fact]
    public void 点在选区外_保留原来的选区()
    {
        var interaction = new CaptureInteraction();
        interaction.Select(new PxRect(10, 10, 50, 50), Screen, 1);
        interaction.MouseDown(new PxPoint(500, 500), 1, Screen, 1);
        interaction.MouseUp(new PxRect(400, 400, 200, 200));

        Assert.Equal(new PxRect(10, 10, 50, 50), interaction.Selection);
    }

    [Fact]
    public void 选区内拖动是移动_拖手柄是调整大小()
    {
        var interaction = new CaptureInteraction();
        interaction.Select(new PxRect(100, 100, 100, 100), Screen, 1);
        interaction.MouseDown(new PxPoint(150, 150), 1, Screen, 1);
        interaction.MouseDragged(new PxPoint(160, 140));
        interaction.MouseUp(null);
        Assert.Equal(new PxRect(110, 90, 100, 100), interaction.Selection);

        interaction.MouseDown(new PxPoint(210, 190), 1, Screen, 1);
        interaction.MouseDragged(new PxPoint(260, 240));
        interaction.MouseUp(null);
        Assert.Equal(new PxRect(110, 90, 150, 150), interaction.Selection);
    }

    [Fact]
    public void 双击选区_确认()
    {
        var interaction = new CaptureInteraction();
        interaction.Select(new PxRect(100, 100, 100, 100), Screen, 1);
        Assert.Equal(CaptureInteraction.Effect.Confirm, interaction.MouseDown(new PxPoint(150, 150), 2, Screen, 1));
    }

    [Fact]
    public void 取消_先清掉选区_再退出()
    {
        var interaction = new CaptureInteraction();
        interaction.Select(new PxRect(100, 100, 100, 100), Screen, 1);

        Assert.Equal(CaptureInteraction.Effect.None, interaction.Cancel());
        Assert.Null(interaction.Selection);
        Assert.Equal(CaptureInteraction.Effect.Exit, interaction.Cancel());
    }

    [Fact]
    public void 方向键_移动或调整大小1像素()
    {
        var interaction = new CaptureInteraction();
        interaction.Select(new PxRect(100, 100, 100, 100), Screen, 1);
        interaction.Nudge(1, 0, resize: false);
        Assert.Equal(new PxRect(101, 100, 100, 100), interaction.Selection);
        interaction.Nudge(0, -1, resize: true);
        Assert.Equal(new PxRect(101, 100, 100, 99), interaction.Selection);
    }

    [Fact]
    public void 第二块屏幕上框选_选区限制在那块屏幕里()
    {
        var right = new PxRect(1000, 0, 1500, 1000);
        var interaction = new CaptureInteraction();
        interaction.MouseDown(new PxPoint(1100, 100), 1, right, 1.5);
        interaction.MouseDragged(new PxPoint(900, 200)); // 拖回左边屏幕
        interaction.MouseUp(null);

        Assert.Equal(new PxRect(1000, 100, 100, 100), interaction.Selection);
        Assert.Equal(right, interaction.Bounds);
    }
}
