using System.Text.Json;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>标注几何(MAIO AnnotationGeometryTests,同样的输入和期望;缩放比例 1,所以 pt = 像素)。</summary>
public class AnnotationGeometryTests
{
    private const double Tolerance = 4;
    private static readonly AnnotationColor Red = AnnotationColors.Red;

    private static Annotation Make(AnnotationShape shape, double width = 4) => new(shape, Red, width);

    private static PointD P(double x, double y) => new(x, y);

    [Fact]
    public void 空心矩形只在边上命中()
    {
        var rect = Make(new RectangleShape(new RectD(0, 0, 100, 100), false, false));
        Assert.True(rect.Contains(P(1, 50), Tolerance));
        Assert.False(rect.Contains(P(50, 50), Tolerance));
        Assert.True(Make(new RectangleShape(new RectD(0, 0, 100, 100), true, false)).Contains(P(50, 50), Tolerance));
    }

    [Fact]
    public void 椭圆_直线_画笔的命中()
    {
        var ellipse = Make(new EllipseShape(new RectD(0, 0, 200, 100), false));
        Assert.True(ellipse.Contains(P(100, 1), Tolerance));
        Assert.False(ellipse.Contains(P(100, 50), Tolerance));
        Assert.False(ellipse.Contains(P(2, 2), Tolerance));
        var line = Make(new LineShape(P(0, 0), P(100, 100), false));
        Assert.True(line.Contains(P(52, 48), Tolerance));
        Assert.False(line.Contains(P(80, 20), Tolerance));
        Assert.True(Make(new PenShape([P(0, 0), P(50, 0), P(50, 50)])).Contains(P(51, 25), Tolerance));
    }

    [Fact]
    public void 移动保留id_手柄调整大小()
    {
        var rect = Make(new RectangleShape(new RectD(10, 10, 20, 20), false, false));
        var moved = rect.Moved(new VectorD(5, -5));
        Assert.Equal(rect.Id, moved.Id);
        Assert.Equal(new RectangleShape(new RectD(15, 5, 20, 20), false, false), moved.Shape);
        Assert.Equal(new RectangleShape(new RectD(10, 10, 40, 50), false, false),
            rect.Resized(new AnnotationHandle.Frame(SelectionHandle.BottomRight), P(50, 60)).Shape);

        var arrow = Make(new ArrowShape(P(0, 0), P(10, 0)));
        Assert.Equal(new AnnotationHandle.End(), arrow.HandleAt(P(11, 1), 7));
        Assert.Equal(new ArrowShape(P(0, 0), P(0, 30)), arrow.Resized(new AnnotationHandle.End(), P(0, 30)).Shape);
    }

    [Fact]
    public void 换算成图片坐标()
    {
        var counter = Make(new CounterShape(P(110, 220), 3, 10));
        var local = counter.Transformed(new VectorD(-100, -200), 2);
        Assert.Equal(new CounterShape(P(20, 40), 3, 20), local.Shape);
        Assert.Equal(8, local.LineWidth);
    }

    [Fact]
    public void Shift吸附()
    {
        var snapped = Snapping.Angle(P(0, 0), P(100, 10));
        Assert.True(Math.Abs(snapped.Y) < 0.001);
        Assert.True(Math.Abs(snapped.X - Math.Sqrt(100 * 100 + 10 * 10)) < 0.001);
        var diagonal = Snapping.Angle(P(0, 0), P(100, 90));
        Assert.True(Math.Abs(diagonal.X - diagonal.Y) < 0.001);
        Assert.Equal(P(-30, 30), Snapping.Square(P(0, 0), P(-30, 10)));
    }

    [Fact]
    public void 标注可以存成JSON再读回来()
    {
        Annotation[] items =
        [
            Make(new TextShape(new TextBox(P(0, 0), "你好", 18, true, TextDecoration.Outline))),
            Make(new MosaicShape(new RectD(1, 2, 3, 4), 8)),
            Make(new HighlighterShape([P(0, 0), P(5, 5)])),
        ];
        var json = JsonSerializer.Serialize(items);
        var decoded = JsonSerializer.Deserialize<Annotation[]>(json)!;
        Assert.Equal(items, decoded);
    }
}

/// <summary>撤销重做(MAIO AnnotationDocumentTests)。</summary>
public class AnnotationDocumentTests
{
    private static Annotation Rect(double x) => new(new RectangleShape(new RectD(x, 0, 10, 10), true, false), AnnotationColors.Red, 2);

    [Fact]
    public void 撤销和重做恢复快照()
    {
        var document = new AnnotationDocument();
        document.Add(Rect(0));
        document.Add(Rect(20));
        Assert.Equal(2, document.Items.Count);
        document.Undo();
        Assert.Single(document.Items);
        document.Redo();
        Assert.Equal(2, document.Items.Count);
        document.Undo();
        document.Add(Rect(40));
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void 最多撤销100步()
    {
        var document = new AnnotationDocument();
        for (var i = 0; i < 120; i++)
        {
            document.Add(Rect(i));
        }

        for (var i = 0; i < 200; i++)
        {
            document.Undo();
        }

        Assert.Equal(20, document.Items.Count);
    }

    [Fact]
    public void 连续调大小只算一步撤销()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Pen);
        editor.MouseDown(new PointD(0, 0), 1, new RectD(0, 0, 100, 100));
        editor.MouseDragged(new PointD(50, 50), false);
        editor.MouseUp();
        for (var value = 5.0; value <= 20; value++)
        {
            editor.SetSize(value);
        }

        Assert.Equal(20, editor.Document.Items[0].LineWidth);
        editor.Document.Undo();
        Assert.Equal(4, editor.Document.Items[0].LineWidth);
        editor.SetSize(500);
        Assert.Equal(40, editor.Document.Items[0].LineWidth);
    }

    [Fact]
    public void 撤销后被删掉的对象不再选中()
    {
        var document = new AnnotationDocument();
        var item = Rect(0);
        document.Add(item);
        document.SelectedId = item.Id;
        document.Undo();
        Assert.Null(document.SelectedId);
    }

    [Fact]
    public void 命中最上层的对象()
    {
        var document = new AnnotationDocument();
        var bottom = Rect(0);
        var top = Rect(5);
        document.Add(bottom);
        document.Add(top);
        Assert.Equal(top.Id, document.Hit(new PointD(7, 5), 4)!.Id);
    }

    [Fact]
    public void 滚轮_平滑滚动的一格只算一步_停顿或换方向重新开始()
    {
        var smooth = new WheelStepper();
        Assert.Equal(1, Enumerable.Range(0, 15).Sum(i => smooth.Feed(6, 10 + i * 0.02)));

        var stepper = new WheelStepper();
        Assert.Equal(1, stepper.Feed(120, 0));
        Assert.Equal(1, stepper.Feed(120, 0.5));
        Assert.Equal(-1, stepper.Feed(-120, 0.55));
        Assert.Equal(0, stepper.Feed(0, 0.6));

        var continuous = new WheelStepper();
        Assert.Equal(5, Enumerable.Range(0, 30).Sum(i => continuous.Feed(20, i * 0.016)));
    }
}
