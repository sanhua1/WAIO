using System.Numerics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure.Rendering;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 选区里要画的标注(MAIO renderAnnotations 的参数)。坐标是全局物理像素。
/// <paramref name="Outline"/>、<paramref name="Handles"/>:选中对象的虚线框和手柄;<paramref name="TextPreview"/>:正在输入的文字的描边 / 底色。
/// </summary>
internal sealed record AnnotationScene(
    IReadOnlyList<Annotation> Items, RectD Selection, EffectSource Source, EffectCache Cache,
    RectD? Outline, IReadOnlyList<PointD> Handles, (Domain.Annotation.TextBox Box, AnnotationColor Color)? TextPreview);

/// <summary>遮罩上的工具栏、标注画布、文字输入框的宿主部分。</summary>
internal sealed partial class OverlayWindow
{
    private readonly CaptureToolbar _toolbar = new();
    private readonly CanvasControl _annotations = new() { IsHitTestVisible = false };
    private AnnotationScene? _scene;

    /// <summary>工具栏的大小(物理像素,按"三行"计,MAIO placeToolbar)。</summary>
    public (int Width, int Height) MeasureToolbar()
    {
        var size = _toolbar.MeasureBase();
        return ((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling((size.Height + CaptureToolbar.OptionsRowHeight) * Scale));
    }

    /// <summary>文字输入框放到遮罩上(局部 DIP 坐标)。</summary>
    public void PlaceEditor(UIElement editor, double x, double y)
    {
        if (!_canvas.Children.Contains(editor))
        {
            _canvas.Children.Insert(_canvas.Children.IndexOf(_toolbar.Root), editor);
        }

        Canvas.SetLeft(editor, x);
        Canvas.SetTop(editor, y);
    }

    public void RemoveEditor(UIElement editor) => _canvas.Children.Remove(editor);

    /// <summary>全局像素 → 本显示器的局部 DIP。</summary>
    public (double X, double Y) ToLocalDip(PointD point) => ((point.X - Display.Bounds.X) / Scale, (point.Y - Display.Bounds.Y) / Scale);

    private void BuildAnnotationLayers()
    {
        _toolbar.Command += command => Handler?.OnToolbar(command);
        _toolbar.FocusReturned += FocusInput;
        _annotations.Draw += OnDrawAnnotations;
    }

    private void RenderToolbar(PxPoint? at, ToolbarState? state, double scale)
    {
        var toolbar = _toolbar.Root;
        if (at is not { } point || state is null)
        {
            toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        _toolbar.Update(state);
        toolbar.Visibility = Visibility.Visible;
        Canvas.SetLeft(toolbar, point.X / scale);
        Canvas.SetTop(toolbar, point.Y / scale);
    }

    private void RenderAnnotations(AnnotationScene? scene, double scale)
    {
        _annotations.Width = Display.Bounds.Width / scale;
        _annotations.Height = Display.Bounds.Height / scale;
        var wasVisible = _scene is not null;
        _scene = scene;
        _annotations.Visibility = scene is null ? Visibility.Collapsed : Visibility.Visible;
        if (scene is not null || wasVisible)
        {
            _annotations.Invalidate();
        }
    }

    /// <summary>画布按全局像素画:平移到本显示器、再缩回 DIP(画布按显示器的缩放比例输出,所以 1 像素对 1 像素)。</summary>
    private void OnDrawAnnotations(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_scene is not { } scene)
        {
            return;
        }

        var ds = args.DrawingSession;
        var scale = (float)Scale;
        ds.Transform = Matrix3x2.CreateTranslation(-Display.Bounds.X, -Display.Bounds.Y) * Matrix3x2.CreateScale(1 / scale);
        AnnotationRenderer.DrawComposite(ds, scene.Items, scene.Selection, scene.Source, scene.Cache);
        if (scene.TextPreview is { } preview)
        {
            using (ds.CreateLayer(1, AnnotationRenderer.ToRect(scene.Selection)))
            {
                TextRendering.Draw(ds, preview.Box, preview.Color, scene.Source.Scale, decorationOnly: true);
            }
        }

        if (scene.Outline is { } outline)
        {
            // 选中对象:白色虚线框(带一圈暗色衬底,任何背景上都看得见)+ 手柄
            var rect = AnnotationRenderer.ToRect(outline);
            using var dashed = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle { CustomDashStyle = [4f, 3f] };
            ds.DrawRectangle(rect, AnnotationRenderer.Rgba(150, 0, 0, 0), 2 * scale);
            ds.DrawRectangle(rect, AnnotationRenderer.Rgba(255, 255, 255, 255), scale, dashed);
            foreach (var handle in scene.Handles)
            {
                var center = new Vector2((float)handle.X, (float)handle.Y);
                ds.FillCircle(center, 3.5f * scale, AnnotationRenderer.Rgba(255, 255, 255, 255));
                ds.DrawCircle(center, 3.5f * scale, AnnotationRenderer.Rgba(255, 0, 0x78, 0xD4), scale);
            }
        }
    }
}
