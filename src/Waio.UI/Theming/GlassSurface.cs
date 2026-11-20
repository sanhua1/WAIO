using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Waio.UI.Theming;

/// <summary>
/// 一个窗口的玻璃层(ADR-0027):垫在窗口原有内容下面的一个 Grid,上面挂一个合成 SpriteVisual,
/// 内容是"窗口背后那块屏幕"经过 裁剪 → 边缘延伸 → 高斯模糊 → 鲜艳度 → 色调罩层 后的样子。
/// 不依赖系统的透明效果:窗口本身是不透明的,玻璃是画出来的。
/// </summary>
internal sealed class GlassSurface
{
    /// <summary>
    /// 玻璃层。自带一层不透明的色调底色:玻璃还没画出来(或某次没画上)时窗口也是实的,
    /// 不会变成只剩系统一像素描边的透明框(屏幕上莫名出现的小框)。
    /// </summary>
    private readonly Grid _layer = new() { IsHitTestVisible = false };
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _base = new();
    private readonly SpriteVisual _visual;
    private readonly CompositionSurfaceBrush _brush;
    private readonly CropEffect _crop = new();
    private readonly BorderEffect _border = new() { ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp };
    private readonly GaussianBlurEffect _blur = new() { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
    private readonly ColorMatrixEffect _color = new() { ClampOutput = true };
    private CompositionDrawingSurface? _surface;

    public GlassSurface(Window window, bool popup)
    {
        Window = window;
        Popup = popup;
        Hwnd = WindowNative.GetWindowHandle(window);

        // 把原来的内容放进一个新根里,玻璃层在最底下(同一 Grid 里先加的在下面)
        var content = window.Content;
        window.Content = null;
        var root = new Grid();
        root.Children.Add(_layer);
        if (content is not null)
        {
            root.Children.Add(content);
        }

        window.Content = root;
        window.SystemBackdrop = null;

        _base.Color = ColorText.WithAlpha(Glass.Current.TintFor(Glass.IsDark(window)), 1);
        _layer.Background = _base;

        var compositor = ElementCompositionPreview.GetElementVisual(_layer).Compositor;
        _brush = compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;
        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        ElementCompositionPreview.SetElementChildVisual(_layer, _visual);
        _layer.SizeChanged += (_, e) =>
        {
            _visual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            Render();
        };

        _border.Source = _crop;
        _blur.Source = _border;
        _color.Source = _blur;
    }

    public Window Window { get; }

    /// <summary>显示时先藏起来、玻璃画好再露出(由 <see cref="Glass.Attach"/> 设置)。</summary>
    public GlassReveal Reveal { get; set; } = null!;

    public nint Hwnd { get; }

    public bool Popup { get; }

    /// <summary>当前所在显示器的截屏(由 <see cref="Glass"/> 分配)。</summary>
    public GlassCapture? Capture { get; set; }

    public Compositor Compositor => _visual.Compositor;

    public bool IsVisible => GlassNative.IsWindowVisible(Hwnd) && !GlassNative.IsIconic(Hwnd);

    /// <summary>上一次轮询时是否可见(用来发现"刚显示出来"但没收到事件的情况)。</summary>
    public bool WasVisible { get; set; }

    public nint Monitor => GlassNative.MonitorFromWindow(Hwnd, GlassNative.MonitorDefaultToNearest);

    /// <summary>按最新一帧和当前参数重画玻璃。只在 UI 线程调用。</summary>
    public void Render()
    {
        if (!IsVisible || Glass.GraphicsFor(Compositor) is not { } graphics)
        {
            return;
        }

        try
        {
            var client = GlassNative.ClientOnScreen(Hwnd);
            if (client.Width <= 0 || client.Height <= 0)
            {
                return;
            }

            var size = new Windows.Foundation.Size(client.Width, client.Height);
            if (_surface is null)
            {
                _surface = graphics.CreateDrawingSurface(size, Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
                _brush.Surface = _surface;
            }
            else if (_surface.Size != size)
            {
                CanvasComposition.Resize(_surface, size);
            }

            using var session = CanvasComposition.CreateDrawingSession(_surface);
            Draw(session, client);
        }
        catch (Exception ex)
        {
            Glass.LogFailure(ex, "绘制玻璃");
        }
    }

    private void Draw(CanvasDrawingSession session, GlassNative.Rect client)
    {
        var theme = Glass.Current;
        var tint = theme.TintFor(Glass.IsDark(Window));
        var whole = new Windows.Foundation.Rect(0, 0, client.Width, client.Height);

        // 底色:截图还没到、窗口跑出屏幕、高对比度时,玻璃就是这块不透明的色调
        _base.Color = ColorText.WithAlpha(tint, 1);
        session.Clear(ColorText.WithAlpha(tint, 1));
        var latest = Capture?.Latest;
        if (latest is null || HighContrast())
        {
            return;
        }

        var monitor = Capture!.Bounds;
        var scale = monitor.Width > 0 ? latest.SizeInPixels.Width / (double)monitor.Width : 1.0 / GlassCapture.Downscale;
        var region = GlassMath.SourceRegion(client, monitor, scale);
        var sigma = theme.BlurRadius / 2 * scale;
        if (GlassMath.PaddedRegion(region, sigma, latest.SizeInPixels.Width, latest.SizeInPixels.Height) is not { } padded)
        {
            return;
        }

        _crop.Source = latest;
        _crop.SourceRectangle = padded;
        _blur.BlurAmount = (float)Math.Max(0.5, sigma);
        var m = GlassMath.SaturationMatrix(theme.Saturation);
        _color.ColorMatrix = new Matrix5x4
        {
            M11 = m[0], M12 = m[1], M13 = m[2], M14 = m[3],
            M21 = m[4], M22 = m[5], M23 = m[6], M24 = m[7],
            M31 = m[8], M32 = m[9], M33 = m[10], M34 = m[11],
            M41 = m[12], M42 = m[13], M43 = m[14], M44 = m[15],
            M51 = m[16], M52 = m[17], M53 = m[18], M54 = m[19],
        };

        // 小图上模糊好的区域放大铺满窗口(放大前已经模糊过,线性插值看不出像素)
        session.DrawImage(_color, whole, region, 1, CanvasImageInterpolation.Linear);
        session.FillRectangle(whole, ColorText.WithAlpha(tint, theme.TintOpacity));
    }

    private static AccessibilitySettings? s_accessibility;

    private static bool HighContrast()
    {
        try
        {
            return (s_accessibility ??= new AccessibilitySettings()).HighContrast;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
