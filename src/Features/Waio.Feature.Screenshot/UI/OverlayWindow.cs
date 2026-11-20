using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.UI;
using Windows.Graphics;
using WinRT.Interop;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 一个显示器上的截图遮罩(MAIO OverlayPanel + OverlayCanvas):冻结画面 + 选区外变暗 + 边框和手柄 + 尺寸标签 + 放大镜 + 工具栏。
/// 每个显示器一个,启用功能时预先创建、之后复用(MAIO spec 5.3)。拖动时只改几何属性,不重画整张图。
/// 绘制内容由 <see cref="CaptureSession"/> 算好后通过 <see cref="Render"/> 交进来;本类只负责摆放(物理像素 → DIP)。
/// <para>
/// <b>显示方式:窗口一直"显示"着,空闲时停在屏幕外</b>。截图时先在屏幕外换上冻结画面、等 XAML 画好,再挪到显示器上——挪动窗口时 DWM 直接用已经画好的内容,
/// 不闪也不会黑。最初用的是"DWM 隐身(cloak)显示、画好再现身",2026-11-10 用户报告有时整屏黑、只剩十字指针,Alt+Tab 才恢复:
/// 判断是 WinUI 把隐身的窗口当成不可见、暂停了渲染,现身后有时没有恢复(推断,未在真机确认)。
/// </para>
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    /// <summary>停在屏幕外时离显示器顶边的距离(物理像素)。不碰任何显示器,所以 DPI 不变;也远离 -32000(最小化窗口的位置)。</summary>
    private const int ParkDistance = 8000;

    private static readonly SolidColorBrush DimBrush = new(ColorHelper.FromArgb(0x73, 0, 0, 0)); // 45% 黑(MAIO)
    private static readonly SolidColorBrush White = new(Colors.White);

    private readonly OverlaySurface _surface = new();
    private readonly Canvas _canvas = new();
    private readonly Image _frozen = new() { Stretch = Stretch.Fill };

    /// <summary>
    /// 选区外变暗:上、下、左、右四块矩形。不用"整屏减去选区"的偶数填充路径——WinUI 里改路径子几何的 Rect 不会触发重绘,
    /// 用户看到的是整屏都不暗(2026-11-10 反馈)。
    /// </summary>
    private readonly Rectangle[] _dims = new Rectangle[4];

    private readonly Rectangle _border = new() { StrokeThickness = 1.5 };
    private readonly Ellipse[] _handles = new Ellipse[8];
    private readonly Border _sizeBadge;
    private readonly TextBlock _sizeText = new() { Foreground = White, FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI") };
    private readonly MagnifierView _magnifier = new();
    private WriteableBitmap? _bitmap;

    public OverlayWindow(DisplayInfo display)
    {
        Display = display;
        var accent = new SolidColorBrush(AccentColor());
        _border.Stroke = accent;
        _sizeBadge = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xBF, 0, 0, 0)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3), Child = _sizeText, Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_frozen);
        _canvas.Children.Add(_annotations); // 选区里的底图 + 标注(Win2D),在变暗层下面
        for (var i = 0; i < _dims.Length; i++)
        {
            _dims[i] = new Rectangle { Fill = DimBrush, IsHitTestVisible = false };
            _canvas.Children.Add(_dims[i]);
        }

        _canvas.Children.Add(_border);
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Ellipse { Width = 8, Height = 8, Fill = White, Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false };
            _canvas.Children.Add(_handles[i]);
        }

        _canvas.Children.Add(_sizeBadge);
        _canvas.Children.Add(_magnifier.Root);
        _canvas.Children.Add(_toolbar.Root);
        BuildAnnotationLayers();
        _surface.Content = _canvas;
        Content = _surface;

        WindowChrome.MakeOverlay(this, activatable: true);
        Native.MakeBareSurface(Handle);
        WireInput();
        Place(display);
        AppWindow.Show(activateWindow: false); // 停在屏幕外"显示"着:XAML 提前建好渲染资源,截图时只需挪过去
    }

    public DisplayInfo Display { get; private set; }

    public nint Handle => WindowNative.GetWindowHandle(this);

    public OverlaySurface Surface => _surface;

    /// <summary>
    /// 显示器的缩放比例。不用 XamlRoot.RasterizationScale:窗口停在屏幕外时 XAML 不知道它将去哪块显示器;
    /// 每个遮罩只服务一块显示器,两者相等。
    /// </summary>
    private double Scale => Display.Scale;

    /// <summary>
    /// 显示器位置或缩放变化时更新(MAIO panel(for:)):先摆到显示器上(让窗口拿到那块显示器的 DPI),
    /// 再按物理尺寸摆一次(DPI 变化时系统可能按比例改过大小),最后停到屏幕外。窗口此时如果是显示状态会闪一下,只在显示器变化时发生。
    /// </summary>
    public void Place(DisplayInfo display)
    {
        Display = display;
        var b = display.Bounds;
        AppWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        AppWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        Park();
    }

    /// <summary>换上冻结画面(窗口在屏幕外)。同步拷贝像素(4K 约 33 MB,几毫秒),尺寸不变时复用位图。</summary>
    public void SetImage(PixelBuffer image)
    {
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show(activateWindow: false);
        }

        if (_bitmap is null || _bitmap.PixelWidth != image.Width || _bitmap.PixelHeight != image.Height)
        {
            _bitmap = new WriteableBitmap(image.Width, image.Height);
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Pixels, 0, image.Width * image.Height * 4);
        }

        _bitmap.Invalidate();
        _frozen.Source = _bitmap;
        _magnifier.Source = image;
    }

    /// <summary>挪到显示器上(XAML 已经在屏幕外画好了冻结画面)。</summary>
    public void MoveOnScreen()
    {
        AppWindow.Move(new PointInt32(Display.Bounds.X, Display.Bounds.Y));
        Native.BringToTop(Handle); // 遮罩是提前建好的,任务栏等置顶窗口后来被激活过就会排到它前面、盖住选框(2026-11-12 用户反馈)
    }

    /// <summary>结束截图:先挪回屏幕外(用户立刻看不到),再释放冻结画面的引用(MAIO:结束截图后立即释放)。</summary>
    public void HideOverlay()
    {
        Park();
        _frozen.Source = null;
        _magnifier.Source = null;
        Render(OverlayFrame.Empty);
    }

    private void Park()
    {
        var b = Display.Bounds;
        AppWindow.Move(new PointInt32(b.X, Math.Max(b.Y - b.Height - ParkDistance, -30000)));
    }

    /// <summary>按算好的状态摆放各个元素(参数是本显示器内的局部物理像素)。</summary>
    public void Render(OverlayFrame frame)
    {
        var scale = Scale;
        var width = Display.Bounds.Width / scale;
        var height = Display.Bounds.Height / scale;
        _frozen.Width = width;
        _frozen.Height = height;
        if (frame.Hole is { } hole)
        {
            var r = Dip(hole, scale);
            Dim(0, 0, 0, width, r.Y);
            Dim(1, 0, r.Y + r.Height, width, height - r.Y - r.Height);
            Dim(2, 0, r.Y, r.X, r.Height);
            Dim(3, r.X + r.Width, r.Y, width - r.X - r.Width, r.Height);
            _border.Visibility = Visibility.Visible;
            Canvas.SetLeft(_border, r.X - 0.75);
            Canvas.SetTop(_border, r.Y - 0.75);
            _border.Width = r.Width + 1.5;
            _border.Height = r.Height + 1.5;
        }
        else
        {
            Dim(0, 0, 0, width, height);
            Dim(1, 0, 0, 0, 0);
            Dim(2, 0, 0, 0, 0);
            Dim(3, 0, 0, 0, 0);
            _border.Visibility = Visibility.Collapsed;
        }

        for (var i = 0; i < _handles.Length; i++)
        {
            var visible = i < frame.Handles.Count;
            _handles[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                Canvas.SetLeft(_handles[i], frame.Handles[i].X / scale - 4);
                Canvas.SetTop(_handles[i], frame.Handles[i].Y / scale - 4);
            }
        }

        RenderBadge(frame, scale);
        _magnifier.Render(frame.Magnifier, scale);
        RenderToolbar(frame.Toolbar, frame.ToolbarState, scale);
        RenderAnnotations(frame.Scene, scale);
    }

    /// <summary>尺寸标签的大小(物理像素),用来算它放在哪(OverlayLayout.SizeLabelFrame)。</summary>
    public (int Width, int Height) MeasureBadge(string text)
    {
        _sizeText.Text = text;
        _sizeBadge.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = Scale;
        return ((int)Math.Ceiling(_sizeBadge.DesiredSize.Width * scale), (int)Math.Ceiling(_sizeBadge.DesiredSize.Height * scale));
    }

    private void Dim(int index, double x, double y, double width, double height)
    {
        var rect = _dims[index];
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
    }

    private void RenderBadge(OverlayFrame frame, double scale)
    {
        if (frame.SizeText is not { } text || frame.SizeFrame is not { } at)
        {
            _sizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        _sizeText.Text = text;
        _sizeBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizeBadge, at.X / scale);
        Canvas.SetTop(_sizeBadge, at.Y / scale);
    }

    private static Windows.Foundation.Rect Dip(PxRect r, double scale) => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);

    private static Windows.UI.Color AccentColor() =>
        Application.Current?.Resources.TryGetValue("SystemAccentColor", out var value) == true && value is Windows.UI.Color color
            ? color
            : ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4);

    private static class Native
    {
        private const int GwlStyle = -16;
        private const long WsPopup = 0x80000000;
        private const long WsClipChildren = 0x02000000;
        private const long WsVisible = 0x10000000;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;

        /// <summary>纯 WS_POPUP、不圆角、没有系统描边:遮罩要严丝合缝地盖住整个显示器。</summary>
        public static void MakeBareSurface(nint hwnd)
        {
            var visible = GetWindowLongPtrW(hwnd, GwlStyle).ToInt64() & WsVisible;
            SetWindowLongPtrW(hwnd, GwlStyle, new nint(WsPopup | WsClipChildren | visible));
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
            var doNotRound = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
            var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
        }

        /// <summary>提到所有置顶窗口的最前面(HWND_TOPMOST),不激活、不移动。</summary>
        public static void BringToTop(nint hwnd) => _ = SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE

        [DllImport("user32.dll")]
        private static extern nint GetWindowLongPtrW(nint hwnd, int index);

        [DllImport("user32.dll")]
        private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    }
}

/// <summary>
/// 一个显示器上要画的东西(局部物理像素,原点是该显示器左上角)。
/// <paramref name="Hole"/> 为 null 时整屏变暗;<paramref name="Handles"/> 是 8 个手柄的中心(空 = 不显示手柄);
/// <paramref name="Toolbar"/> 是工具栏左上角(null = 不显示);<paramref name="Scene"/> 是选区里的标注(null = 没有标注,不画)。
/// </summary>
internal sealed record OverlayFrame(
    PxRect? Hole, IReadOnlyList<PxPoint> Handles, string? SizeText, PxRect? SizeFrame, MagnifierState? Magnifier, PxPoint? Toolbar = null,
    ToolbarState? ToolbarState = null, AnnotationScene? Scene = null)
{
    public static readonly OverlayFrame Empty = new(null, [], null, null, null);
}
