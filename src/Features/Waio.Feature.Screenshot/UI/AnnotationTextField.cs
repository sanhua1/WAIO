using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Screenshot.Domain.Annotation;
using Waio.Feature.Screenshot.Infrastructure.Rendering;
using Windows.System;
using Windows.UI.Core;
using Box = Waio.Feature.Screenshot.Domain.Annotation.TextBox;
using XamlTextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 在遮罩上原地输入文字(MAIO AnnotationTextField,spec 4.2:支持中文输入法;Ctrl+⏎、Esc 或点击别处结束)。
/// <para>
/// 和 MAIO 的差异:MAIO 的输入框文字透明、由标注图层实时画出最终效果;WinUI 的输入框在输入法组字时,组字中的文字不一定出现在 Text 里,
/// 透明的话组字时会什么都看不到。所以这里输入框自己显示文字(同字体、同颜色),标注图层只在它下面画描边 / 底色;结束输入后由标注图层按最终效果画。
/// </para>
/// </summary>
internal sealed class AnnotationTextField
{
    private readonly XamlTextBox _input;
    private Box? _box;
    private OverlayWindow? _host;
    private double _scale = 1;
    private bool _finishing;

    public AnnotationTextField()
    {
        _input = new XamlTextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            MinWidth = 0,
            MinHeight = 0,
            FontFamily = new FontFamily(TextRendering.FontFamily),
        };

        // 去掉输入框各种状态下的背景、边框、下划线:只剩文字和光标
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                 })
        {
            _input.Resources[key] = clear;
        }

        _input.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        _input.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _input.TextChanged += (_, _) => Changed?.Invoke();
        _input.PreviewKeyDown += OnPreviewKeyDown;
        _input.LostFocus += (_, _) =>
        {
            // 点工具栏(改样式)会让输入框失去焦点:由会话决定是否结束;这里只在焦点去了别处时结束
            if (!_finishing && IsEditing && _input.XamlRoot is not null && FocusManager.GetFocusedElement(_input.XamlRoot) is null)
            {
                Committed?.Invoke();
            }
        };
    }

    /// <summary>结束编辑(Ctrl+⏎ / Esc)。</summary>
    public event Action? Committed;

    /// <summary>内容变化(重画预览)。</summary>
    public event Action? Changed;

    public Guid? EditingId { get; private set; }

    public AnnotationColor Color { get; private set; } = AnnotationColors.Red;

    public bool IsEditing => _box is not null;

    /// <summary>正在输入的文字(已测量大小)。</summary>
    public Box? CurrentBox
    {
        get
        {
            if (_box is not { } box)
            {
                return null;
            }

            var current = box with { Text = _input.Text.Replace("\r\n", "\n").Replace('\r', '\n') };
            var (width, height) = TextRendering.Size(current, _scale);
            return current with { Width = width, Height = height };
        }
    }

    public void Begin(Box box, AnnotationColor color, Guid? editing, OverlayWindow host, double scale)
    {
        EditingId = editing;
        _host = host;
        _scale = scale;
        _input.Text = box.Text;
        Restyle(box, color);
        _input.Focus(FocusState.Programmatic);
        _input.SelectAll();
    }

    /// <summary>修改样式,保留内容、光标和选中范围。</summary>
    public void Restyle(Box box, AnnotationColor color)
    {
        _box = box;
        Color = color;
        var foreground = box.Decoration == TextDecoration.Background && box.BackgroundColor is null
            ? TextAppearance.AutomaticBackground(color)
            : color;
        _input.Foreground = new SolidColorBrush(AnnotationRenderer.ToColor(foreground));
        _input.Resources["TextControlForeground"] = _input.Foreground;
        _input.Resources["TextControlForegroundPointerOver"] = _input.Foreground;
        _input.Resources["TextControlForegroundFocused"] = _input.Foreground;
        _input.FontSize = Math.Max(1, box.FontSize / _scale);
        _input.FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(box.Bold ? 700 : 400) };
        _input.Padding = new Thickness(TextRendering.PaddingX, TextRendering.PaddingY, TextRendering.PaddingX, TextRendering.PaddingY);
        if (_host is { } host)
        {
            var (x, y) = host.ToLocalDip(box.Origin);
            host.PlaceEditor(_input, x, y);
        }
    }

    /// <summary>结束编辑,返回最终的文字框(已测量)和文字颜色。只有空白时文字为空(会被删除)。</summary>
    public (Box Box, AnnotationColor Color, Guid? Id)? Finish()
    {
        if (CurrentBox is not { } box)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(box.Text))
        {
            box = box with { Text = "" };
        }

        var id = EditingId;
        _finishing = true;
        _box = null;
        EditingId = null;
        _host?.RemoveEditor(_input);
        _host?.FocusInput();
        _host = null;
        _finishing = false;
        return (box, Color, id);
    }

    /// <summary>Ctrl+⏎ 或 Esc 结束编辑(输入法正在组字时按键先交给输入法,不会到这里)。</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape || (e.Key == VirtualKey.Enter && ctrl))
        {
            e.Handled = true;
            Committed?.Invoke();
        }
    }
}
