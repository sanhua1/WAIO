using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Waio.Feature.Screenshot.Domain.Annotation;
using Rect = Windows.Foundation.Rect;
using Windows.Graphics.DirectX;
using static Waio.Feature.Screenshot.UI.PinNative;

namespace Waio.Feature.Screenshot.UI;

/// <summary>贴图窗口的操作(由 <see cref="PinManager"/> 交给控制器处理,MAIO PinAction)。</summary>
internal enum PinAction
{
    Copy,
    Save,
    Edit,
    Ocr,
    Translate,

    /// <summary>复制选中的文字(贴图上选择识别出的文字)。</summary>
    CopyText,

    /// <summary>复制识别出的全部文字。</summary>
    CopyAllText,
    Close,
    CloseAll,
}

/// <summary>
/// 一张钉在屏幕上的贴图(MAIO PinWindow + PinView,spec 3.3、4.4)。
/// 用 Win32 分层窗口(<c>UpdateLayeredWindow</c>,逐像素透明)而不是 WinUI 窗口:透明背景、自绘发光阴影、整体透明度都是它原生支持的;
/// 内容用 Win2D 画好再交给窗口。拖动用系统的标题栏拖动(命中测试返回 HTCAPTION),所以很顺。
/// 总在最前、不进任务栏和 Alt+Tab。只在 UI 线程上使用(窗口消息由 UI 线程的消息循环分发)。
/// </summary>
internal sealed partial class PinWindow : IDisposable
{
    private readonly Func<PinControls> _controls;
    private readonly WheelStepper _zoomWheel = new();
    private readonly WheelStepper _opacityWheel = new();
    private readonly Windows.UI.Color _accent = AccentColor();
    private CanvasBitmap? _bitmap;
    private nint _hwnd;
    private bool _hovered;
    private bool _active;
    private string? _hint;

    /// <param name="contentOrigin">贴图内容(不含阴影外边距)左上角的全局坐标(物理像素)。</param>
    public PinWindow(PixelBuffer image, double scale, PxPoint contentOrigin, double zoom, string? recordId, bool showsShadow, Func<PinControls> controls)
    {
        Image = image;
        RecordId = recordId;
        _controls = controls;
        State = new PinState { ImageSize = (image.Width, image.Height), Scale = scale, ShowsShadow = showsShadow };
        State = State with { Zoom = Math.Clamp(zoom, PinState.MinZoom, State.ZoomLimit) };
        _hwnd = Create(this);
        Place(contentOrigin);
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>对应的历史记录(MAIO ADR-0012:每张贴图对应一条历史记录)。</summary>
    public string? RecordId { get; set; }

    public PixelBuffer Image { get; private set; }

    public PinState State { get; private set; }

    public event Action<PinWindow, PinAction>? ActionRequested;

    /// <summary>贴图内容的全局矩形(物理像素)。拖动由系统完成,所以每次都从窗口位置算。</summary>
    public PxRect ContentRect
    {
        get
        {
            GetWindowRect(_hwnd, out var r);
            var (w, h) = State.ContentSize;
            return new PxRect(r.Left + State.Margin, r.Top + State.Margin, w, h);
        }
    }

    /// <summary>按当前旋转输出的图片(复制、保存用)。</summary>
    public PixelBuffer OutputImage => Image.Rotated(State.QuarterTurns);

    public void Show() => SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);

    public void Hide() => ShowWindow(_hwnd, SwHide);

    /// <summary>修改状态后重新布局,保持中心不动(MAIO apply)。</summary>
    public void Apply(Func<PinState, PinState> change)
    {
        var old = ContentRect;
        State = change(State);
        var (w, h) = State.ContentSize;
        Place(new PxPoint(old.X + (old.Width - w) / 2, old.Y + (old.Height - h) / 2));
    }

    /// <summary>编辑标注后换成新图,位置保持(MAIO replace)。</summary>
    public void Replace(PixelBuffer image, double scale)
    {
        var origin = ContentRect;
        Image = image;
        ResetText(); // 新图要重新识别(控制器会再启动一次)
        _bitmap?.Dispose();
        _bitmap = null;
        State = State with { ImageSize = (image.Width, image.Height), Scale = scale };
        State = State with { Zoom = Math.Min(State.Zoom, State.ZoomLimit) };
        Place(new PxPoint(origin.X, origin.Y));
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            var hwnd = _hwnd;
            _hwnd = 0;
            Live.Remove(hwnd);
            DestroyWindow(hwnd);
        }

        _bitmap?.Dispose();
        _bitmap = null;
        ReleaseSurface();
    }

    private void Perform(PinAction action) => ActionRequested?.Invoke(this, action);

    /// <summary>以鼠标为中心按步缩放(滚轮一格、- = 一次,每步 5%);鼠标不在贴图上时以中心为准。</summary>
    private void ZoomBy(int steps)
    {
        var rect = ContentRect;
        var cursor = Waio.UI.ScreenLocator.CursorPosition();
        var anchor = rect.Contains(new PxPoint(cursor.X, cursor.Y))
            ? ((cursor.X - rect.X) / (double)rect.Width, (cursor.Y - rect.Y) / (double)rect.Height)
            : (0.5, 0.5);
        var (state, origin) = State.ZoomedBy(steps, anchor, new PxPoint(rect.X, rect.Y));
        State = state;
        Flash($"{Math.Round(State.Zoom * 100)}%", origin);
    }

    private void SetOpacity(PinState state)
    {
        State = state;
        Flash($"透明度 {Math.Round(State.Opacity * 100)}%", null);
    }

    /// <summary>短暂显示提示(缩放比例、透明度),1 秒后消失。</summary>
    private void Flash(string text, PxPoint? origin)
    {
        _hint = text;
        SetTimer(_hwnd, HintTimer, 1000, 0);
        Place(origin ?? new PxPoint(ContentRect.X, ContentRect.Y));
    }

    // ---- 绘制 ----

    /// <summary>按状态画好整张窗口(阴影 + 图片 + 提示),放到 <paramref name="contentOrigin"/>。</summary>
    private void Place(PxPoint contentOrigin)
    {
        if (_hwnd == 0)
        {
            return;
        }

        var (width, height) = State.WindowSize;
        var bytes = Draw(width, height);
        Present(bytes, width, height, new PxPoint(contentOrigin.X - State.Margin, contentOrigin.Y - State.Margin));
    }

    private byte[] Draw(int width, int height)
    {
        var device = CanvasDevice.GetSharedDevice();
        var margin = State.Margin;
        var (cw, ch) = State.ContentSize;
        var content = new Rect(margin, margin, cw, ch);
        using var target = new CanvasRenderTarget(device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(AnnotationRenderer.Rgba(0, 0, 0, 0));
            if (State.ShowsShadow)
            {
                // 系统主题色的发光(MAIO:悬停或选中时更亮);关掉阴影后就是一张纯粹的图
                using var shape = new CanvasCommandList(ds);
                using (var shapeDs = shape.CreateDrawingSession())
                {
                    shapeDs.FillRectangle(content, AnnotationRenderer.Rgba(255, 255, 255, 255));
                }

                var strength = _hovered || _active ? 0.95 : 0.6;
                using var glow = new ShadowEffect
                {
                    Source = shape, BlurAmount = (float)(5 * State.Scale),
                    ShadowColor = AnnotationRenderer.Rgba((byte)(255 * strength), _accent.R, _accent.G, _accent.B),
                };
                ds.DrawImage(glow);
            }

            _bitmap ??= CanvasBitmap.CreateFromBytes(device, Image.Pixels, Image.Width, Image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            var (iw, ih) = (Image.Width * State.Zoom, Image.Height * State.Zoom);
            ds.Transform = Matrix3x2.CreateRotation((float)(State.QuarterTurns * Math.PI / 2))
                * Matrix3x2.CreateTranslation((float)(margin + cw / 2.0), (float)(margin + ch / 2.0));
            var interpolation = State.Zoom >= 2 || State.Zoom == 1 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic;
            ds.DrawImage(_bitmap, new Rect(-iw / 2, -ih / 2, iw, ih), _bitmap.Bounds, 1, interpolation);
            DrawSelection(ds, iw, ih);
            ds.Transform = Matrix3x2.Identity;
            if (_hint is { } hint)
            {
                DrawHint(ds, hint, content);
            }
        }

        return target.GetPixelBytes();
    }

    private void DrawHint(CanvasDrawingSession ds, string hint, Rect content)
    {
        var s = State.Scale;
        using var format = new CanvasTextFormat { FontFamily = TextRendering.FontFamily, FontSize = (float)(12 * s), FontWeight = new Windows.UI.Text.FontWeight { Weight = 500 } };
        using var layout = new CanvasTextLayout(ds, hint, format, 1000, 100);
        var box = new Rect(content.X + 6 * s, content.Y + 6 * s, layout.LayoutBounds.Width + 12 * s, layout.LayoutBounds.Height + 6 * s);
        ds.FillRoundedRectangle(box, (float)(4 * s), (float)(4 * s), AnnotationRenderer.Rgba(178, 0, 0, 0));
        ds.DrawTextLayout(layout, (float)(box.X + 6 * s), (float)(box.Y + 3 * s), AnnotationRenderer.Rgba(255, 255, 255, 255));
    }

    private static Windows.UI.Color AccentColor()
    {
        try
        {
            return new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch (COMException)
        {
            return AnnotationRenderer.Rgba(255, 0, 0x78, 0xD4);
        }
    }
}
