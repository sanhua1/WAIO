using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Windows.System;

namespace Waio.Feature.Screenshot.UI;

/// <summary>工具栏命令、文字输入、工具栏状态同步(MAIO CaptureSession+Annotation)。</summary>
internal sealed partial class CaptureSession
{
    private ToolbarState? _toolbarState;
    private readonly WheelStepper _counterWheel = new();
    private readonly WheelStepper _sizeWheel = new(30);

    /// <summary>工具栏按钮(MAIO perform)。</summary>
    public void OnToolbar(ToolbarCommand command)
    {
        _editor.Scale = EditorScale;

        // 文字的样式命令:作用于"当前这段文字"(正在输入 > 选中 > 默认)
        if (_toolbarState?.ActiveTool == AnnotationTool.Text && TextChangeFor(command) is { } change)
        {
            ChangeText(change);
            return;
        }

        if (command is ToolbarCommand.TextColorTargetChange target)
        {
            _editor.TextColorTarget = target.Value;
            SyncToolbar();
            Render();
            return;
        }

        if (_textField.IsEditing)
        {
            CommitText();
        }

        switch (command)
        {
            case ToolbarCommand.Cancel:
                Finish(new CaptureOutcome.Cancelled());
                return;
            case ToolbarCommand.Save:
                Confirm(CaptureAction.Save);
                return;
            case ToolbarCommand.Pin:
                Confirm(CaptureAction.Pin);
                return;
            case ToolbarCommand.Ocr:
                Confirm(CaptureAction.Ocr);
                return;
            case ToolbarCommand.Translate:
                Confirm(CaptureAction.Translate);
                return;
            case ToolbarCommand.Scrolling:
                Confirm(CaptureAction.Scrolling);
                return;
            case ToolbarCommand.Record:
                Confirm(CaptureAction.Record);
                return;
            case ToolbarCommand.Copy:
                Confirm(_options.PrimaryAction);
                return;
            case ToolbarCommand.Tool tool:
                _editor.SelectTool(tool.Value);
                break;
            case ToolbarCommand.Color color:
                _editor.SetColor(color.Value);
                break;
            case ToolbarCommand.Size size:
                _editor.SetSize(size.Value);
                break;
            case ToolbarCommand.Option option:
                _editor.SetOptions(s =>
                {
                    switch (option.Value)
                    {
                        case ToolOption.Filled:
                            s.Filled = !s.Filled;
                            break;
                        case ToolOption.Rounded:
                            s.Rounded = !s.Rounded;
                            break;
                        default:
                            s.Dashed = !s.Dashed;
                            break;
                    }
                });
                break;
            case ToolbarCommand.Undo:
                _editor.Document.Undo();
                break;
            case ToolbarCommand.Redo:
                _editor.Document.Redo();
                break;
        }

        SyncToolbar();
        UpdateCursorShape();
        Render();
    }

    private static TextChange? TextChangeFor(ToolbarCommand command) => command switch
    {
        ToolbarCommand.Color c => new TextChange.Color(c.Value),
        ToolbarCommand.Size s => new TextChange.FontSize(s.Value),
        ToolbarCommand.TextBold => new TextChange.ToggleBold(),
        ToolbarCommand.TextDecorationChange d => new TextChange.Decoration(d.Value),
        _ => null,
    };

    /// <summary>当前这段文字的样式:正在输入的 &gt; 选中的 &gt; 下一次新建的。</summary>
    private TextAppearance CurrentTextAppearance
    {
        get
        {
            if (_textField.CurrentBox is { } box)
            {
                return TextAppearance.From(box, _textField.Color, _editor.Scale);
            }

            if (_editor.SelectedText is { } selected)
            {
                return TextAppearance.From(selected.Box, selected.Annotation.Color, _editor.Scale);
            }

            return _editor.DefaultTextAppearance;
        }
    }

    private void ChangeText(TextChange change)
    {
        if (_textField.CurrentBox is { } box)
        {
            var appearance = TextAppearance.From(box, _textField.Color, _editor.Scale).Apply(change, _editor.TextColorTarget);
            _editor.Remember(appearance);
            _textField.Restyle(appearance.AppliedTo(box, _editor.Scale), appearance.Foreground);
        }
        else
        {
            _editor.ChangeText(change, b => TextRendering.Size(b, _editor.Scale));
        }

        SyncToolbar();
        Render();
    }

    /// <summary>把编辑器状态交给工具栏(MAIO syncToolbar)。</summary>
    private void SyncToolbar()
    {
        var active = _textField.IsEditing ? AnnotationTool.Text : _editor.ActiveTool;
        var style = _editor.Style;
        var text = CurrentTextAppearance;
        var textActive = active == AnnotationTool.Text;
        var tool = active ?? AnnotationTool.Rectangle;
        _toolbarState = new ToolbarState(
            _editor.Tool,
            active,
            _editor.Document.CanUndo,
            _editor.Document.CanRedo,
            textActive ? text.PaletteColor(_editor.TextColorTarget) : _editor.PaletteColor,
            textActive ? text.FontSize : _editor.SelectedSize ?? style.Size(tool),
            textActive ? AnnotationTool.Text.SizeRange() : tool.SizeRange(),
            style.Filled,
            style.Rounded,
            style.Dashed,
            text,
            text.ColorTarget(_editor.TextColorTarget),
            _editor.Document.NextCounterNumber,
            _options.PrimaryAction.ConfirmHelp(),
            CapturesScreen: _edit is null);
    }

    // ---- 文字输入 ----

    private void BeginText(TextBox box, AnnotationColor color, Guid? editing)
    {
        var overlay = _overlays.FirstOrDefault(o => o.Display.Bounds == _interaction.Bounds);
        if (overlay is null)
        {
            return;
        }

        _textField.Begin(box, color, editing, overlay, _editor.Scale);
        SyncToolbar();
        Render();
    }

    private void EditText(Guid id)
    {
        if (_editor.Document.Items.FirstOrDefault(a => a.Id == id) is { Shape: TextShape text } annotation)
        {
            BeginText(text.Box, annotation.Color, id);
        }
    }

    private void CommitText()
    {
        if (_textField.Finish() is { } result)
        {
            _editor.CommitText(result.Box, result.Color, result.Id);
            SyncToolbar();
        }
    }

    /// <summary>滚轮(MAIO overlayScrollWheel):序号工具调整下一个编号,其他工具(或选中的对象)调整大小,每步 1 pt。</summary>
    public void OnWheel(int delta)
    {
        var tool = _textField.IsEditing ? AnnotationTool.Text : _editor.ActiveTool;
        if (tool is null || _editor.IsDragging)
        {
            return;
        }

        var adjustsCounter = tool == AnnotationTool.Counter && _editor.Document.Selected is null;
        var time = Environment.TickCount64 / 1000.0;
        var step = adjustsCounter ? _counterWheel.Feed(delta, time) : _sizeWheel.Feed(delta, time);
        if (step == 0)
        {
            return;
        }

        if (adjustsCounter)
        {
            _editor.AdjustCounter(step);
            SyncToolbar();
            Render();
        }
        else
        {
            OnToolbar(new ToolbarCommand.Size((_toolbarState?.Size ?? 0) + step));
        }
    }


    /// <summary>标注相关的按键(MAIO handleAnnotationKey):工具字母、撤销重做、删除选中对象、方向键移动选中对象。</summary>
    private bool HandleAnnotationKey(VirtualKey key, bool ctrl, bool shift)
    {
        if (_interaction.Selection is null || _interaction.IsDragging || _editor.IsDragging)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Z when ctrl && !shift:
                _editor.Document.Undo();
                return true;
            case VirtualKey.Z when ctrl && shift:
            case VirtualKey.Y when ctrl && !shift:
                _editor.Document.Redo();
                return true;
            case VirtualKey.Delete or VirtualKey.Back when !ctrl && !shift:
                return _editor.DeleteSelected();
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when !ctrl && !shift && _editor.Document.Selected is { } selected:
                var (dx, dy) = Direction(key);
                _editor.Document.Checkpoint();
                _editor.Document.Replace(selected.Moved(new VectorD(dx, dy)));
                return true;
        }

        if (ctrl || shift || key < VirtualKey.A || key > VirtualKey.Z || AnnotationTools.ForKey((char)key) is not { } tool)
        {
            return false;
        }

        _editor.SelectTool(tool);
        return true;
    }

}
