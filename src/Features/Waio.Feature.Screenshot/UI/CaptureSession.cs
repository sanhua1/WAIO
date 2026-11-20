using Microsoft.UI.Xaml.Media;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>截图完成后的输出动作(MAIO CaptureAction)。</summary>
internal enum CaptureAction
{
    Copy,
    Save,

    /// <summary>贴图(同时复制)。</summary>
    Pin,

    /// <summary>识别文字,打开结果窗口;不复制图片。</summary>
    Ocr,

    /// <summary>识别文字后直接交给翻译窗口;不复制图片。</summary>
    Translate,

    /// <summary>长截图:在选区上开始滚动拼接(ADR-0020)。选区上已经画的标注不带过去(MAIO 同)。</summary>
    Scrolling,

    /// <summary>录屏:在选区上打开录屏控制条(ADR-0021)。</summary>
    Record,
}

internal static class CaptureActionExtensions
{
    public static bool RecognizesText(this CaptureAction action) => action is CaptureAction.Ocr or CaptureAction.Translate;

    /// <summary>✓ 按钮的提示(MAIO confirmHelp)。</summary>
    public static string ConfirmHelp(this CaptureAction action) => action switch
    {
        CaptureAction.Ocr => "识别文字(⏎)",
        CaptureAction.Translate => "翻译(⏎)",
        CaptureAction.Scrolling => "开始长截图(⏎)",
        CaptureAction.Record => "录屏(⏎)",
        _ => "复制(⏎)",
    };
}

/// <summary>截图结果(MAIO CaptureResult)。</summary>
/// <param name="Original">裁剪后的原图(物理像素,不含标注)。</param>
/// <param name="Scale">截图所在显示器的缩放比例。</param>
/// <param name="Frame">截图在屏幕上的位置(全局物理像素),贴图时原地出现。</param>
internal sealed record CaptureResult(PixelBuffer Original, double Scale, PxRect Frame)
{
    /// <summary>带标注的最终图;没有标注时为 null(最终图就是原图)。</summary>
    public PixelBuffer? Rendered { get; init; }

    /// <summary>标注对象,坐标相对截图左上角(物理像素)。</summary>
    public IReadOnlyList<Annotation> Annotations { get; init; } = [];

    public PixelBuffer FinalImage => Rendered ?? Original;
}

/// <summary>
/// 重新编辑已有截图的标注(从贴图或历史打开,MAIO EditContext):选区固定为图片的位置,底图是原图而不是屏幕。
/// <paramref name="Source"/> 的 Origin 是图片显示的位置,Zoom 是缩小显示的倍数;<paramref name="Annotations"/> 已换算成全局坐标。
/// 会话结束时负责释放 <paramref name="Source"/>。
/// </summary>
internal sealed record EditContext(EffectSource Source, PxRect Rect, IReadOnlyList<Annotation> Annotations);

/// <summary>一次截图的结果(MAIO CaptureOutcome)。</summary>
internal abstract record CaptureOutcome
{
    public sealed record Cancelled : CaptureOutcome;

    /// <summary>复制颜色值。</summary>
    public sealed record Color(string Text) : CaptureOutcome;

    public sealed record Image(CaptureResult Result, CaptureAction Action) : CaptureOutcome;
}

/// <summary>
/// 一次区域截图的交互(MAIO CaptureSession,ADR-0011 的流程):在冻结画面上框选、识别窗口和控件、取色,最后交出结果。
/// 输出(复制、保存、写历史)由 <see cref="ScreenshotController"/> 负责。鼠标键盘见 <c>.Input.cs</c>,识别和绘制见 <c>.Rendering.cs</c>。
/// 只在 UI 线程上使用。
/// </summary>
internal sealed partial class CaptureSession : ICaptureInput
{
    private readonly IReadOnlyList<FrozenDisplay> _frozen;
    private readonly IReadOnlyList<OverlayWindow> _overlays;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly Options _options;
    private readonly Callbacks _callbacks;
    private readonly CaptureInteraction _interaction = new();
    private readonly AnnotationEditor _editor = new();
    private readonly AnnotationTextField _textField = new();
    private readonly EffectCache _effectCache = new();
    private EffectSource? _source;
    private readonly EditContext? _edit;
    private PxRect? _highlight;
    private PxPoint _cursor;
    private SampledColor? _color;
    private bool _finished;

    public CaptureSession(
        IReadOnlyList<FrozenDisplay> frozen, IReadOnlyList<OverlayWindow> overlays, IReadOnlyList<WindowInfo> windows,
        Options options, Callbacks callbacks, EditContext? edit = null)
    {
        _frozen = frozen;
        _overlays = overlays;
        _windows = windows;
        _options = options;
        _callbacks = callbacks;
        _edit = edit;
        _cursor = CursorNow();
        _editor.Style = options.ToolStyle.Clone();
        if (edit is not null && frozen.FirstOrDefault(f => f.Display.Bounds.Intersects(edit.Rect)) is { } display)
        {
            _interaction.Select(edit.Rect, display.Display.Bounds, display.Display.Scale);
            _editor.Document.Load(edit.Annotations);
        }

        _textField.Changed += Render;
        _textField.Committed += () =>
        {
            CommitText();
            Render();
        };
    }

    /// <param name="AlwaysDetectControls">总是识别控件;否则按住 Ctrl 才识别。</param>
    internal sealed record Options(bool AlwaysDetectControls, bool ShowMagnifier, ColorFormat ColorFormat)
    {
        /// <summary>标注工具上次用的颜色、大小、选项。</summary>
        public ToolStyle ToolStyle { get; init; } = new();

        /// <summary>⏎、双击选区做什么。</summary>
        public CaptureAction PrimaryAction { get; init; } = CaptureAction.Copy;

        public ColorFormat ColorFormat { get; set; } = ColorFormat;
    }

    internal sealed record Callbacks(Action<ColorFormat> ColorFormatChanged, Action<ToolStyle> ToolStyleChanged, Action<CaptureOutcome> Finished);

    /// <summary>
    /// 显示遮罩:每个显示器在屏幕外换上冻结画面,等 XAML 画好再挪到显示器上(不闪、不黑),然后让鼠标所在的遮罩接收键盘(MAIO show)。
    /// </summary>
    public async Task ShowAsync()
    {
        for (var i = 0; i < _overlays.Count; i++)
        {
            _overlays[i].Handler = this;
            _overlays[i].SetImage(_frozen[i].Image);
        }

        UpdateColor();
        UpdateHover();
        Render();
        await NextFramesAsync(2);
        if (_finished)
        {
            return;
        }

        foreach (var overlay in _overlays)
        {
            overlay.MoveOnScreen();
        }

        var key = OverlayAt(_cursor) ?? _overlays[0];
        WindowChrome.ActivateForeground(key);
        key.FocusInput();
    }

    public void Finish(CaptureOutcome outcome)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _elementQuery = null;
        _textField.Finish();
        foreach (var overlay in _overlays)
        {
            overlay.Handler = null;
            overlay.HideOverlay();
        }

        _effectCache.Dispose();
        _source?.Dispose();
        _edit?.Source.Dispose();
        _callbacks.ToolStyleChanged(_editor.Style);
        _callbacks.Finished(outcome);
    }

    /// <summary>选区所在显示器的冻结画面。</summary>
    private FrozenDisplay? SelectionDisplay => _frozen.FirstOrDefault(f => f.Display.Bounds == _interaction.Bounds);

    /// <summary>重新编辑时按原图的缩放比例(新画的对象和原来的大小一致);否则按选区所在显示器。</summary>
    private double EditorScale => _edit?.Source.Scale ?? SelectionDisplay?.Display.Scale ?? 1;

    private CaptureResult? MakeResult()
    {
        if (_edit is { } edit)
        {
            return MakeEditResult(edit);
        }

        if (_interaction.Selection is not { } selection || SelectionDisplay is not { } display)
        {
            return null;
        }

        var original = display.Image.Crop(SelectionGeometry.PixelRect(selection, display.Display.Bounds));
        var items = _editor.Document.Items;
        if (items.Count == 0 || Source is not { } source)
        {
            return new CaptureResult(original, display.Display.Scale, selection);
        }

        // 输出和屏幕上用同一套绘制(MAIO renderOutput);标注存成相对截图左上角的坐标
        return new CaptureResult(original, display.Display.Scale, selection)
        {
            Rendered = AnnotationRenderer.RenderOutput(items, selection, source),
            Annotations = [.. items.Select(a => a.Transformed(new VectorD(-selection.X, -selection.Y), 1))],
        };
    }

    /// <summary>重新编辑的结果:按原图的分辨率输出,标注换算回相对原图左上角的像素坐标(MAIO makeEditResult)。</summary>
    private CaptureResult MakeEditResult(EditContext edit)
    {
        var source = edit.Source;
        var items = _editor.Document.Items;
        var origin = new VectorD(-source.Origin.X, -source.Origin.Y);
        return new CaptureResult(source.Image, source.Scale * source.Zoom, edit.Rect)
        {
            Rendered = items.Count == 0 ? null : AnnotationRenderer.RenderEditOutput(items, source),
            Annotations = [.. items.Select(a => a.Transformed(origin, source.Zoom))],
        };
    }

    /// <summary>选区所在显示器的冻结画面,用来画底图、马赛克和模糊(换了显示器就换一份);重新编辑时是原图。</summary>
    private EffectSource? Source
    {
        get
        {
            if (_edit is { } edit)
            {
                return edit.Source;
            }

            if (SelectionDisplay is not { } display)
            {
                return null;
            }

            if (_source is null || _source.Image != display.Image)
            {
                _source?.Dispose();
                _source = new EffectSource(display.Image, new PxPoint(display.Display.Bounds.X, display.Display.Bounds.Y), display.Display.Scale);
            }

            return _source;
        }
    }

    private void Confirm(CaptureAction action)
    {
        // 重新编辑已有截图时不能长截图、录屏(选区是图片,不是屏幕;MAIO 同)
        if (action is CaptureAction.Scrolling or CaptureAction.Record && _edit is not null)
        {
            return;
        }

        if (_textField.IsEditing)
        {
            CommitText();
        }

        if (MakeResult() is { } result)
        {
            Finish(new CaptureOutcome.Image(result, action));
        }
    }

    private void CopyColor()
    {
        if (_color is { } color)
        {
            Finish(new CaptureOutcome.Color(_options.ColorFormat.Format(color)));
        }
    }

    private FrozenDisplay? DisplayAt(PxPoint point) =>
        _frozen.FirstOrDefault(f => f.Display.Bounds.Contains(point))
        ?? _frozen.MinBy(f => Distance(f.Display.Bounds, point));

    private OverlayWindow? OverlayAt(PxPoint point) =>
        DisplayAt(point) is { } display ? _overlays[IndexOf(display)] : null;

    private int IndexOf(FrozenDisplay display)
    {
        for (var i = 0; i < _frozen.Count; i++)
        {
            if (ReferenceEquals(_frozen[i], display))
            {
                return i;
            }
        }

        return 0;
    }

    private static double Distance(PxRect rect, PxPoint point)
    {
        var clamped = SelectionGeometry.Clamp(point, rect);
        return Math.Sqrt(Math.Pow(clamped.X - point.X, 2) + Math.Pow(clamped.Y - point.Y, 2));
    }

    private static PxPoint CursorNow()
    {
        var p = ScreenLocator.CursorPosition();
        return new PxPoint(p.X, p.Y);
    }

    /// <summary>等 XAML 画完 <paramref name="count"/> 帧。</summary>
    private static Task NextFramesAsync(int count)
    {
        var done = new TaskCompletionSource();
        var remaining = count;
        void OnRendering(object? sender, object e)
        {
            if (--remaining <= 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                done.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        return Task.WhenAny(done.Task, Task.Delay(250)); // 兜底:万一不出帧也不能一直隐身
    }
}
