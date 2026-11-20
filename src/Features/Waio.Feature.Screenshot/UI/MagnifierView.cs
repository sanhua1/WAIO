using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;

namespace Waio.Feature.Screenshot.UI;

/// <summary>放大镜要显示的内容。<paramref name="PixelX"/>/<paramref name="PixelY"/> 是鼠标处像素在冻结画面里的坐标;<paramref name="Frame"/> 是局部物理像素。</summary>
internal sealed record MagnifierState(int PixelX, int PixelY, IReadOnlyList<string> Lines, SampledColor? Color, PxRect Frame);

/// <summary>
/// 放大镜(MAIO MagnifierLayer,spec 4.1):15×15 个物理像素放大 8 倍,中心格子是鼠标处的像素,十字线标出所在行列;下方显示坐标和颜色。
/// 放大图按最近邻直接在像素里生成(每格 = 8 pt 换算成的整数物理像素),再 1:1 显示——WinUI 的 Image 缩放只有平滑插值,会糊。
/// </summary>
internal sealed class MagnifierView
{
    public const int Pixels = 15;
    public const double CellDip = 8;
    public const double InfoHeightDip = 54;

    private static readonly Windows.UI.Color CrossColor = ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4);

    private readonly Image _zoom = new() { Stretch = Stretch.None };
    private readonly Border _swatch = new() { Width = 12, Height = 12, BorderBrush = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(1) };
    private readonly TextBlock _info = new()
    {
        Foreground = new SolidColorBrush(Colors.White), FontSize = 11, FontFamily = new FontFamily("Consolas, Segoe UI"),
        LineHeight = 15, FontWeight = FontWeights.Normal,
    };

    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];
    private int _cell;
    private (int X, int Y) _rendered = (int.MinValue, int.MinValue);

    public MagnifierView()
    {
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 4, 6, 4) };
        _swatch.VerticalAlignment = VerticalAlignment.Top;
        _swatch.Margin = new Thickness(0, 17, 0, 0);
        info.Children.Add(_swatch);
        info.Children.Add(_info);
        var stack = new StackPanel();
        stack.Children.Add(_zoom);
        stack.Children.Add(info);
        Root = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xCC, 0, 0, 0)), CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = stack, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
    }

    public Border Root { get; }

    public PixelBuffer? Source { get; set; }

    /// <summary>放大镜的大小(物理像素),给 OverlayLayout.MagnifierFrame 用。</summary>
    public static (int Width, int Height) SizeFor(double scale)
    {
        var side = Pixels * Cell(scale);
        return (side, side + (int)Math.Round(InfoHeightDip * scale));
    }

    public void Render(MagnifierState? state, double scale)
    {
        if (state is null || Source is not { } source)
        {
            Root.Visibility = Visibility.Collapsed;
            return;
        }

        var cell = Cell(scale);
        if (_bitmap is null || cell != _cell)
        {
            _cell = cell;
            _bitmap = new WriteableBitmap(Pixels * cell, Pixels * cell);
            _pixels = new byte[Pixels * cell * Pixels * cell * 4];
            _zoom.Source = _bitmap;
            _zoom.Width = Pixels * cell / scale;
            _zoom.Height = Pixels * cell / scale;
            _rendered = (int.MinValue, int.MinValue);
        }

        if (_rendered != (state.PixelX, state.PixelY))
        {
            _rendered = (state.PixelX, state.PixelY);
            Draw(source, state.PixelX, state.PixelY);
        }

        _info.Text = string.Join("\n", state.Lines);
        _swatch.Background = state.Color is { } c ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, c.Red, c.Green, c.Blue)) : null;
        Root.Width = Pixels * cell / scale + 2;
        Root.Visibility = Visibility.Visible;
        Canvas.SetLeft(Root, state.Frame.X / scale);
        Canvas.SetTop(Root, state.Frame.Y / scale);
    }

    private static int Cell(double scale) => Math.Max(1, (int)Math.Round(CellDip * scale));

    /// <summary>最近邻放大 + 十字线(所在行列 30% 强调色)+ 中心格白框(MAIO ZoomLayer)。</summary>
    private void Draw(PixelBuffer source, int centerX, int centerY)
    {
        var side = Pixels * _cell;
        var half = Pixels / 2;
        for (var gy = 0; gy < Pixels; gy++)
        {
            for (var gx = 0; gx < Pixels; gx++)
            {
                var color = source.ColorAt(centerX - half + gx, centerY - half + gy) ?? new SampledColor(0, 0, 0);
                if ((gx == half) != (gy == half))
                {
                    color = Blend(color, CrossColor, 0.3);
                }

                FillCell(gx, gy, side, color);
            }
        }

        // 中心格的白色 1 像素边框
        var start = half * _cell;
        for (var i = 0; i < _cell; i++)
        {
            Put(start + i, start, side);
            Put(start + i, start + _cell - 1, side);
            Put(start, start + i, side);
            Put(start + _cell - 1, start + i, side);
        }

        using (var stream = _bitmap!.PixelBuffer.AsStream())
        {
            stream.Write(_pixels, 0, _pixels.Length);
        }

        _bitmap.Invalidate();
    }

    private void FillCell(int gx, int gy, int side, SampledColor color)
    {
        var packed = PixelBuffer.Pack(color);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan());
        for (var y = gy * _cell; y < (gy + 1) * _cell; y++)
        {
            span.Slice(y * side + gx * _cell, _cell).Fill(packed);
        }
    }

    private void Put(int x, int y, int side) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan())[y * side + x] = 0xFFFFFFFFu;

    private static SampledColor Blend(SampledColor under, Windows.UI.Color over, double alpha) => new(
        (byte)Math.Round(under.Red * (1 - alpha) + over.R * alpha),
        (byte)Math.Round(under.Green * (1 - alpha) + over.G * alpha),
        (byte)Math.Round(under.Blue * (1 - alpha) + over.B * alpha));
}
