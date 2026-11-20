using Waio.Feature.Screenshot.Domain.Annotation;

namespace Waio.Feature.Screenshot.UI;

/// <summary>工具栏的命令(MAIO ToolbarCommand)。</summary>
internal abstract record ToolbarCommand
{
    public sealed record Tool(AnnotationTool Value) : ToolbarCommand;

    public sealed record Color(AnnotationColor Value) : ToolbarCommand;

    /// <summary>大小(pt)。</summary>
    public sealed record Size(double Value) : ToolbarCommand;

    public sealed record Option(ToolOption Value) : ToolbarCommand;

    public sealed record TextBold : ToolbarCommand;

    public sealed record TextDecorationChange(TextDecoration Value) : ToolbarCommand;

    public sealed record TextColorTargetChange(TextColorTarget Value) : ToolbarCommand;

    public sealed record Undo : ToolbarCommand;

    public sealed record Redo : ToolbarCommand;

    /// <summary>贴图(同时复制)。</summary>
    public sealed record Pin : ToolbarCommand;

    public sealed record Save : ToolbarCommand;

    /// <summary>识别文字(Ctrl+O)。</summary>
    public sealed record Ocr : ToolbarCommand;

    /// <summary>截图翻译(Ctrl+T)。</summary>
    public sealed record Translate : ToolbarCommand;

    /// <summary>长截图(Ctrl+L)。</summary>
    public sealed record Scrolling : ToolbarCommand;

    /// <summary>录屏(Ctrl+R)。</summary>
    public sealed record Record : ToolbarCommand;

    public sealed record Cancel : ToolbarCommand;

    /// <summary>✓:主动作(普通截图是复制)。</summary>
    public sealed record Copy : ToolbarCommand;
}

/// <summary>工具的开关选项(第三行)。</summary>
internal enum ToolOption
{
    Filled,
    Rounded,
    Dashed,
}

/// <summary>工具栏要显示的状态(MAIO ToolbarModel,由会话的 SyncToolbar 算好交给工具栏)。</summary>
internal sealed record ToolbarState(
    AnnotationTool? Tool,
    AnnotationTool? ActiveTool,
    bool CanUndo,
    bool CanRedo,
    AnnotationColor PaletteColor,
    double Size,
    (double Min, double Max) SizeRange,
    bool Filled,
    bool Rounded,
    bool Dashed,
    TextAppearance Text,
    TextColorTarget TextColorTarget,
    int NextCounter,
    string ConfirmHelp,
    bool CapturesScreen = true);
