namespace Waio.Feature.Screenshot.Domain.Annotation;

/// <summary>对文字样式的一次修改(MAIO TextChange)。</summary>
public abstract record TextChange
{
    public sealed record Color(AnnotationColor Value) : TextChange;

    /// <summary>字号(pt)。</summary>
    public sealed record FontSize(double Value) : TextChange;

    public sealed record ToggleBold : TextChange;

    public sealed record Decoration(TextDecoration Value) : TextChange;
}

/// <summary>
/// 一段文字的全部样式(MAIO TextAppearance)。工具栏永远显示并修改"当前这段文字"的样式:
/// 正在输入的 &gt; 选中的 &gt; 下一次新建的(默认样式)。修改时同时记成默认样式。字号单位是 pt。
/// </summary>
public sealed record TextAppearance(AnnotationColor Foreground, AnnotationColor Background, TextDecoration Decoration, bool Bold, double FontSize)
{
    /// <summary>从已有的文字读出样式(<paramref name="scale"/> 把像素字号换回 pt)。没有背景色时按文字颜色取黑或白。</summary>
    public static TextAppearance From(TextBox box, AnnotationColor color, double scale) =>
        new(color, box.BackgroundColor ?? AutomaticBackground(color), box.Decoration, box.Bold, box.FontSize / scale);

    /// <summary>色板改的是哪种颜色:外观为"无"时只有文字颜色。</summary>
    public TextColorTarget ColorTarget(TextColorTarget preferred) => Decoration == TextDecoration.Plain ? TextColorTarget.Foreground : preferred;

    /// <summary>色板上高亮的颜色。</summary>
    public AnnotationColor PaletteColor(TextColorTarget preferred) => ColorTarget(preferred) == TextColorTarget.Background ? Background : Foreground;

    public TextAppearance Apply(TextChange change, TextColorTarget preferred) => change switch
    {
        TextChange.Color c when ColorTarget(preferred) == TextColorTarget.Background => this with { Background = c.Value },
        TextChange.Color c => this with { Foreground = c.Value },
        TextChange.FontSize f => this with { FontSize = AnnotationTool.Text.ClampedSize(f.Value) },
        TextChange.ToggleBold => this with { Bold = !Bold },
        TextChange.Decoration d => this with { Decoration = d.Value },
        _ => this,
    };

    /// <summary>把样式写回文字框(内容和位置不变;大小需要调用方重新测量)。</summary>
    public TextBox AppliedTo(TextBox box, double scale) =>
        box with { FontSize = FontSize * scale, Bold = Bold, Decoration = Decoration, BackgroundColor = Background };

    /// <summary>浅色文字配黑色,深色文字配白色。</summary>
    public static AnnotationColor AutomaticBackground(AnnotationColor color) =>
        color.Luminance > 0.65 ? AnnotationColors.Black : AnnotationColors.White;
}

/// <summary>样式:颜色、大小、工具选项、文字样式(MAIO AnnotationEditor+Style / +Text)。都会同时作用于选中的对象。</summary>
public sealed partial class AnnotationEditor
{
    /// <summary>色板上高亮的颜色(文字的颜色由 TextAppearance 决定,不走这里)。</summary>
    public AnnotationColor PaletteColor => Document.Selected?.Color ?? Style.ColorFor(ActiveTool ?? AnnotationTool.Rectangle);

    /// <summary>选中对象自己的大小(pt;工具栏显示它,而不是默认值)。</summary>
    public double? SelectedSize => Document.Selected is not { } selected ? null : selected.Shape switch
    {
        TextShape s => s.Box.FontSize / Scale,
        MosaicShape s => s.BlockSize / Scale,
        BlurShape s => s.Radius / Scale,
        CounterShape s => s.Radius / Scale,
        _ => selected.LineWidth / Scale,
    };

    public void SetColor(AnnotationColor color)
    {
        Style.SetColor(color, ActiveTool);
        UpdateSelected(a => a.Tool().UsesColor() ? a with { Color = color } : a);
    }

    /// <summary>设置大小(pt,连续可调)。连续调整同一个对象只算一步撤销。</summary>
    public void SetSize(double value)
    {
        if (ActiveTool is not { } tool)
        {
            return;
        }

        Style.SetSize(value, tool);
        var size = Style.Size(tool) * Scale;
        UpdateSelected(a => a.Shape switch
        {
            TextShape => a, // 文字走 ChangeText
            MosaicShape s => a with { Shape = s with { BlockSize = size } },
            BlurShape s => a with { Shape = s with { Radius = size } },
            CounterShape s => a with { Shape = s with { Radius = size } },
            _ => a with { LineWidth = size },
        }, "size");
    }

    /// <summary>修改工具选项(实心、圆角、虚线),同时作用于选中的对象。</summary>
    public void SetOptions(Action<ToolStyle> change)
    {
        change(Style);
        var style = Style;
        UpdateSelected(a => a.Shape switch
        {
            RectangleShape s => a with { Shape = s with { Filled = style.Filled, Rounded = style.Rounded } },
            EllipseShape s => a with { Shape = s with { Filled = style.Filled } },
            LineShape s => a with { Shape = s with { Dashed = style.Dashed } },
            _ => a,
        });
    }

    // ---- 文字 ----

    /// <summary>下一次新建文字使用的样式。</summary>
    public TextAppearance DefaultTextAppearance =>
        new(Style.Color, Style.TextBackgroundColor, Style.TextDecoration, Style.Bold, Style.Size(AnnotationTool.Text));

    /// <summary>选中的文字。</summary>
    public (Annotation Annotation, TextBox Box)? SelectedText =>
        Document.Selected is { Shape: TextShape text } selected ? (selected, text.Box) : null;

    /// <summary>把样式记成默认样式(下一次新建文字使用)。</summary>
    public void Remember(TextAppearance appearance)
    {
        Style.Color = appearance.Foreground;
        Style.TextBackgroundColor = appearance.Background;
        Style.TextDecoration = appearance.Decoration;
        Style.Bold = appearance.Bold;
        Style.SetSize(appearance.FontSize, AnnotationTool.Text);
    }

    /// <summary>修改选中的文字;没有选中文字时修改默认样式。<paramref name="measure"/> 重新测量文字框(像素)。</summary>
    public void ChangeText(TextChange change, Func<TextBox, (double Width, double Height)> measure)
    {
        if (SelectedText is not { } selected)
        {
            Remember(DefaultTextAppearance.Apply(change, TextColorTarget));
            return;
        }

        var appearance = TextAppearance.From(selected.Box, selected.Annotation.Color, Scale).Apply(change, TextColorTarget);
        Remember(appearance);
        var updated = appearance.AppliedTo(selected.Box, Scale);
        var (width, height) = measure(updated);
        updated = updated with { Width = width, Height = height };
        UpdateSelected(a => a with { Color = appearance.Foreground, Shape = new TextShape(updated) },
            change is TextChange.FontSize ? "size" : null);
    }

    /// <summary>结束文字编辑:新增、修改,或者(文字为空时)删除。完成后选中这段文字,方便马上改样式。</summary>
    public void CommitText(TextBox box, AnnotationColor color, Guid? editing)
    {
        if (editing is { } id && Document.Items.FirstOrDefault(a => a.Id == id) is { } existing)
        {
            if (box.Text.Length == 0)
            {
                Document.Remove(id);
                return;
            }

            var updated = existing with { Shape = new TextShape(box), Color = color };
            Document.SelectedId = id;
            if (updated != existing)
            {
                Document.Checkpoint();
                Document.Replace(updated);
            }

            return;
        }

        if (box.Text.Length == 0)
        {
            return;
        }

        var annotation = new Annotation(new TextShape(box), color, 0);
        Document.Add(annotation);
        Document.SelectedId = annotation.Id;
    }

    /// <summary>新文字的文字框。</summary>
    public TextBox NewTextBox(PointD point) =>
        DefaultTextAppearance.AppliedTo(new TextBox(point, "", 0, false, TextDecoration.Plain), Scale);
}
