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
using Waio.UI.Theming;
using Windows.Graphics;
using WinRT.Interop;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 长截图时选区旁边的竖条面板(MAIO ScrollCaptureSidebar):已拼高度、完成按钮、提示、长条预览(绿框 = 当前画面在长图中的位置)。
/// 不抢焦点(用户的滚轮要落在下面的程序上),排除在截屏之外。高度随预览变高,最高到工作区底部。只在 UI 线程上使用。
/// </summary>
internal sealed class ScrollCaptureSidebar : Window
{
    /// <summary>面板宽度(DIP,MAIO 180 pt)。</summary>
    public const double WidthDip = 180;

    private const double PaddingDip = 10;
    private const double HeaderDip = 92; // 高度一行 + 提示两行 + 间距,预览从这里往下

    private readonly TextBlock _height = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Image _preview = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Rectangle _marker = new() { Stroke = new SolidColorBrush(Colors.LimeGreen), StrokeThickness = 1 };
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Brush _normal;
    private readonly Brush _warning = new SolidColorBrush(Colors.Orange);
    private readonly PxRect _area;
    private readonly double _scale;
    private WriteableBitmap? _bitmap;
    private int _lastHeight = -1;

    /// <param name="area">面板能占的范围(全局物理像素,<see cref="ScrollCaptureLayout.Sidebar"/>)。</param>
    public ScrollCaptureSidebar(PxRect area, double scale, Action finish)
    {
        _area = area;
        _scale = scale;
        Title = "WAIO 长截图";

        var done = new Button { Content = "完成", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Padding = new Thickness(12, 3, 12, 4) };
        done.Click += (_, _) => finish();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_height);
        Grid.SetColumn(done, 1);
        top.Children.Add(done);
        _normal = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        _canvas.Children.Add(_preview);
        _canvas.Children.Add(_marker);
        var previewFrame = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _canvas,
        };

        var root = new StackPanel { Padding = new Thickness(PaddingDip), Spacing = 8 };
        root.Children.Add(top);
        root.Children.Add(_hint);
        root.Children.Add(previewFrame);
        Content = root;

        WindowChrome.MakeOverlay(this, activatable: false);
        WindowChrome.MakePopupSurface(this, dark: Glass.IsDark());
        Glass.Attach(this, popup: true); // 磨砂玻璃,参数跟随外观设置(ADR-0026)
        _ = ScrollCaptureBorder.SetWindowDisplayAffinity(WindowNative.GetWindowHandle(this), 0x11); // WDA_EXCLUDEFROMCAPTURE
        Update(0, 0, 0, null, null);
    }

    public void ShowPanel() => WindowChrome.ShowWithoutActivation(this);

    /// <summary>刷新显示。<paramref name="preview"/> 为 null 时保留上一次的预览。</summary>
    public void Update(int canvasHeight, int frameHeight, int position, PixelBuffer? preview, string? warning)
    {
        _height.Text = $"{canvasHeight} px";
        _hint.Text = warning ?? "用滚轮往下滚动\nEsc 或 ⏎ 完成";
        _hint.Foreground = warning is null ? _normal : _warning;
        if (preview is not null)
        {
            ShowPreview(preview);
        }

        if (_bitmap is not null)
        {
            var (markerTop, markerHeight) = ScrollCaptureLayout.ViewportMarker(canvasHeight, frameHeight, position, _canvas.Height);
            _marker.Width = _canvas.Width;
            _marker.Height = markerHeight;
            Canvas.SetTop(_marker, markerTop);
        }

        Fit();
    }

    /// <summary>预览按面板宽度显示;太长时按剩下的高度等比缩小。</summary>
    private void ShowPreview(PixelBuffer preview)
    {
        if (_bitmap is null || _bitmap.PixelWidth != preview.Width || _bitmap.PixelHeight != preview.Height)
        {
            _bitmap = new WriteableBitmap(preview.Width, preview.Height);
            _preview.Source = _bitmap;
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(preview.Pixels, 0, preview.Width * preview.Height * 4);
        }

        _bitmap.Invalidate();
        var maxWidth = WidthDip - PaddingDip * 2 - 2;
        var maxHeight = Math.Max(40, _area.Height / _scale - HeaderDip - PaddingDip * 2 - 2);
        var fit = Math.Min(maxWidth / preview.Width, maxHeight / preview.Height);
        _canvas.Width = _preview.Width = Math.Max(1, preview.Width * fit);
        _canvas.Height = _preview.Height = Math.Max(1, preview.Height * fit);
    }

    /// <summary>窗口高度跟着内容走(变化超过几个像素才改,避免每帧都动窗口)。</summary>
    private void Fit()
    {
        var content = HeaderDip + PaddingDip * 2 + (_bitmap is null ? 0 : _canvas.Height + 2);
        var height = (int)Math.Min(_area.Height, Math.Ceiling(content * _scale));
        if (Math.Abs(height - _lastHeight) < 4)
        {
            return;
        }

        if (_lastHeight < 0)
        {
            // 先挪到目标显示器上:跨不同缩放的显示器时系统会按新 DPI 自动缩放窗口,挪过去之后再设尺寸才不会被覆盖
            AppWindow.Move(new PointInt32(_area.X, _area.Y));
        }

        _lastHeight = height;
        AppWindow.MoveAndResize(new RectInt32(_area.X, _area.Y, _area.Width, height));
    }
}
