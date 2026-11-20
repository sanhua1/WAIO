using Waio.Feature.Screenshot.Domain.Annotation;

namespace Waio.Feature.Screenshot.Tests;

/// <summary>标注编辑(MAIO AnnotationEditorTests,同样的输入和期望)。</summary>
public class AnnotationEditorTests
{
    private static readonly RectD Selection = new(0, 0, 500, 500);

    private static PointD P(double x, double y) => new(x, y);

    private static (double, double) Measure(TextBox _) => (30, 20);

    [Fact]
    public void 画矩形_加入并选中_Shift画正方形()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Rectangle);
        Assert.IsType<AnnotationEditor.Outcome.Handled>(editor.MouseDown(P(10, 10), 1, Selection));
        editor.MouseDragged(P(60, 30), constrained: true);
        Assert.NotNull(editor.Draft);
        editor.MouseUp();

        var item = Assert.Single(editor.Document.Items);
        Assert.Equal(item.Id, editor.Document.SelectedId);
        Assert.Equal(new RectD(10, 10, 50, 50), Assert.IsType<RectangleShape>(item.Shape).Frame);
    }

    [Fact]
    public void 太小的拖动不保留()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Arrow);
        editor.MouseDown(P(10, 10), 1, Selection);
        editor.MouseDragged(P(11, 10), false);
        editor.MouseUp();
        Assert.True(editor.Document.IsEmpty);
    }

    [Fact]
    public void 带工具点在选区外_不归标注处理()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Pen);
        Assert.IsType<AnnotationEditor.Outcome.NotHandled>(editor.MouseDown(P(600, 10), 1, Selection));
    }

    [Fact]
    public void 没有工具时单击选中_拖动移动_一步撤销()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Ellipse);
        editor.MouseDown(P(100, 100), 1, Selection);
        editor.MouseDragged(P(200, 200), false);
        editor.MouseUp();
        editor.SelectTool(AnnotationTool.Ellipse); // 再选一次 = 取消工具
        Assert.Null(editor.Tool);

        Assert.IsType<AnnotationEditor.Outcome.NotHandled>(editor.MouseDown(P(400, 400), 1, Selection));
        Assert.Null(editor.Document.SelectedId);
        Assert.IsType<AnnotationEditor.Outcome.Handled>(editor.MouseDown(P(150, 101), 1, Selection));
        editor.MouseDragged(P(160, 111), false);
        editor.MouseUp();
        Assert.Equal(108, editor.Document.Items[0].Bounds().X);
        editor.Document.Undo();
        Assert.Equal(98, editor.Document.Items[0].Bounds().X);
    }

    [Fact]
    public void 序号自动加一_滚轮调整下一个()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Counter);
        editor.MouseDown(P(10, 10), 1, Selection);
        editor.MouseUp();
        editor.Document.SelectedId = null;
        editor.MouseDown(P(50, 10), 1, Selection);
        editor.MouseUp();
        Assert.Equal(3, editor.Document.NextCounterNumber);
        editor.AdjustCounter(5);
        Assert.Equal(8, editor.Document.NextCounterNumber);
        editor.AdjustCounter(-100);
        Assert.Equal(1, editor.Document.NextCounterNumber);
    }

    [Fact]
    public void 改样式作用于选中的对象_每次修改一步撤销()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Line);
        editor.MouseDown(P(0, 0), 1, Selection);
        editor.MouseDragged(P(100, 0), false);
        editor.MouseUp();
        editor.SetColor(AnnotationColors.Blue);
        editor.SetSize(10);
        editor.SetOptions(s => s.Dashed = true);

        var line = editor.Document.Items[0];
        Assert.Equal(AnnotationColors.Blue, line.Color);
        Assert.Equal(10, line.LineWidth);
        Assert.Equal(new LineShape(P(0, 0), P(100, 0), true), line.Shape);
        Assert.Equal(AnnotationColors.Blue, editor.Style.Color);
        editor.Document.Undo();
        Assert.Equal(10, editor.Document.Items[0].LineWidth);
        Assert.Equal(new LineShape(P(0, 0), P(100, 0), false), editor.Document.Items[0].Shape);
    }

    [Fact]
    public void 缩放比例_大小按pt换算成像素()
    {
        var editor = new AnnotationEditor { Scale = 1.5 };
        editor.SelectTool(AnnotationTool.Rectangle);
        editor.MouseDown(P(0, 0), 1, Selection);
        editor.MouseDragged(P(50, 50), false);
        editor.MouseUp();

        Assert.Equal(6, editor.Document.Items[0].LineWidth); // 默认 4 pt
        Assert.Equal(4, editor.SelectedSize);
        editor.SetSize(10);
        Assert.Equal(15, editor.Document.Items[0].LineWidth);
    }

    [Fact]
    public void 文字工具_开始输入_提交_再编辑_清空即删除()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Text);
        Assert.Equal(new AnnotationEditor.Outcome.BeginText(P(5, 5)), editor.MouseDown(P(5, 5), 1, Selection));

        var box = editor.NewTextBox(P(5, 5)) with { Text = "标注", Width = 50, Height = 24 };
        editor.CommitText(box, AnnotationColors.Red, null);
        var id = editor.Document.Items[0].Id;
        Assert.Equal(new AnnotationEditor.Outcome.EditText(id), editor.MouseDown(P(20, 10), 1, Selection));

        editor.CommitText(box with { Text = "" }, AnnotationColors.Red, id);
        Assert.True(editor.Document.IsEmpty);
    }

    [Fact]
    public void 提交的文字被选中_色板和样式改它()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Text);
        editor.CommitText(editor.NewTextBox(P(0, 0)) with { Text = "字" }, AnnotationColors.Red, null);
        Assert.NotNull(editor.SelectedText);

        // 外观为"无":即使选了"背景",色板也改文字颜色
        editor.TextColorTarget = TextColorTarget.Background;
        editor.ChangeText(new TextChange.Color(AnnotationColors.Blue), Measure);
        Assert.Equal(AnnotationColors.Blue, editor.Document.Items[0].Color);

        // 改成底色后,色板改底色,文字颜色不变
        editor.ChangeText(new TextChange.Decoration(TextDecoration.Background), Measure);
        editor.ChangeText(new TextChange.Color(AnnotationColors.Yellow), Measure);
        var (annotation, updated) = editor.SelectedText!.Value;
        Assert.Equal(TextDecoration.Background, updated.Decoration);
        Assert.Equal(AnnotationColors.Yellow, updated.BackgroundColor);
        Assert.Equal(AnnotationColors.Blue, annotation.Color);

        // 粗体切换的是这段文字自己的值,并记成默认样式
        editor.ChangeText(new TextChange.ToggleBold(), Measure);
        Assert.True(editor.SelectedText!.Value.Box.Bold);
        Assert.True(editor.Style.Bold);
        Assert.Equal(AnnotationColors.Yellow, editor.Style.TextBackgroundColor);
        Assert.Equal(TextDecoration.Background, editor.Style.TextDecoration);
        editor.Document.Undo();
        Assert.False(editor.SelectedText!.Value.Box.Bold);
    }

    [Fact]
    public void 没有选中文字时_改的是默认样式()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Text);
        editor.ChangeText(new TextChange.Decoration(TextDecoration.Outline), Measure);
        editor.TextColorTarget = TextColorTarget.Background;
        editor.ChangeText(new TextChange.Color(AnnotationColors.Black), Measure);
        editor.ChangeText(new TextChange.FontSize(36), Measure);

        var box = editor.NewTextBox(P(0, 0));
        Assert.Equal(TextDecoration.Outline, box.Decoration);
        Assert.Equal(AnnotationColors.Black, box.BackgroundColor);
        Assert.Equal(36, box.FontSize);
        Assert.True(editor.Document.IsEmpty);
    }

    [Fact]
    public void 旧文字没有背景色_自动取黑或白()
    {
        var box = new TextBox(P(0, 0), "a", 14, false, TextDecoration.Outline);
        Assert.Equal(AnnotationColors.White, TextAppearance.From(box, AnnotationColors.Red, 1).Background);
        Assert.Equal(AnnotationColors.Black, TextAppearance.From(box, AnnotationColors.Yellow, 1).Background);
    }

    [Fact]
    public void Esc_先取消选中_再取消工具()
    {
        var editor = new AnnotationEditor();
        editor.SelectTool(AnnotationTool.Rectangle);
        editor.MouseDown(P(0, 0), 1, Selection);
        editor.MouseDragged(P(50, 50), false);
        editor.MouseUp();

        Assert.True(editor.Escape());
        Assert.Null(editor.Document.SelectedId);
        Assert.True(editor.Escape());
        Assert.Null(editor.Tool);
        Assert.False(editor.Escape());
    }
}
