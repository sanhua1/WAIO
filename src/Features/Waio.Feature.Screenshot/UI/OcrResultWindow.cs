using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Waio.Kit.Text;
using Waio.UI;
using Waio.UI.Theming;
using Windows.System;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 识别结果窗口(MAIO OCRResultView + OCRResultWindows):可编辑的文字,带"删除换行""翻译""复制"。
/// 每次识别开一个(可以同时开几个对照),放在鼠标所在显示器的正中间,总在最前。Ctrl+W / Esc 关闭。只在 UI 线程上使用。
/// </summary>
internal sealed class OcrResultWindow : Window
{
    /// <summary>按钮的操作(由控制器实现;参数是窗口里当前的文字)。</summary>
    internal sealed record Actions(Action<string> Copy, Action<string> Translate);

    private readonly TextBox _text;
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12 };

    public OcrResultWindow(string text, Actions actions)
    {
        Title = "识别结果";
        _text = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsSpellCheckEnabled = false,
            FontSize = 14, VerticalAlignment = VerticalAlignment.Stretch,
        };

        // 必须在 AcceptsReturn = true 之后再设文字:单行文本框收到多行文字时只保留第一行,之后再改成多行也找不回来
        // (2026-11-11 用户反馈"识别只出来第一行",历史里存的是完整文字)
        _text.Text = text;
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        _text.TextChanged += (_, _) => UpdateCount();

        var lineBreaks = new Button { Content = "删除换行" };
        lineBreaks.Click += (_, _) => _text.Text = TextCleaner.RemovingLineBreaks(_text.Text);
        var translate = new Button { Content = "翻译" };
        translate.Click += (_, _) => actions.Translate(_text.Text);
        var copy = new Button { Content = "复制", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        copy.Click += (_, _) => actions.Copy(_text.Text);

        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_count);
        AddAt(bar, lineBreaks, 1);
        AddAt(bar, translate, 2);
        AddAt(bar, copy, 3);

        var root = new Grid { Padding = new Thickness(12), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_text);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);
        root.KeyDown += OnKeyDown;
        Content = root;
        Glass.Attach(this); // 磨砂玻璃,参数跟随外观设置(ADR-0026)
        UpdateCount();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true; // MAIO .floating
            presenter.IsMinimizable = false;
        }

        var (width, height) = SizeFor(text);
        WindowChrome.CenterOnCurrentMonitor(this, width, height);
    }

    public void ShowAndActivate()
    {
        Activate();
        WindowChrome.ActivateForeground(this);
        _text.Focus(FocusState.Programmatic);
    }

    /// <summary>按文字多少估一个初始大小(DIP,MAIO size(for:))。</summary>
    internal static (double Width, double Height) SizeFor(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var longest = lines.Max(l => l.Sum(c => TextCleaner.IsCjk(c) ? 2 : 1)); // 中文字大约两个英文字符宽
        return (Math.Clamp(longest * 8 + 80, 420, 720), Math.Clamp(lines.Length * 22 + 130, 260, 600));
    }

    private void UpdateCount() => _count.Text = $"{_text.Text.Length} 个字符";

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape || (ctrl && e.Key == VirtualKey.W))
        {
            e.Handled = true;
            Close();
        }
    }

    private static void AddAt(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
