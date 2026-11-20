using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Waio.Feature.Screenshot.Domain.Recording;
using Waio.UI;
using Waio.UI.Theming;
using Windows.System;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 全屏录屏结束后问"保留哪些声音"(ADR-0030,用户要求每次都问)。两项都勾 = 合并成一条音轨。
/// 全程没声音的那一路默认不勾。⏎ / "保存" 按当前勾选保存;关掉窗口或 Esc 也按当前勾选保存(不让一段录屏悄悄丢掉);
/// 只有"放弃"才删除。只在 UI 线程上使用。
/// </summary>
internal sealed class RecordingTracksWindow : Window
{
    /// <summary>录到的一路声音:有没有、是不是全程静音。</summary>
    internal sealed record Source(bool Recorded, bool Silent);

    private readonly CheckBox _system;
    private readonly CheckBox _microphone;
    private readonly Action<bool, bool> _save;
    private readonly Action _discard;
    private bool _answered;

    public RecordingTracksWindow(TimeSpan elapsed, Source system, Source microphone, Action<bool, bool> save, Action discard)
    {
        Title = "录屏结束";
        _save = save;
        _discard = discard;
        _system = Choice("系统声音", system);
        _microphone = Choice("麦克风", microphone);

        var title = new TextBlock
        {
            Text = $"全屏录屏结束({RecordingGeometry.Elapsed(elapsed)})",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        };
        var question = new TextBlock { Text = "保留哪些声音?", Opacity = 0.8 };
        var hint = new TextBlock { Text = "两项都勾选时合并为一条音轨;都不勾就只保存画面。", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        var discardButton = new Button { Content = "放弃录屏" };
        discardButton.Click += (_, _) => Answer(keep: false);
        var saveButton = new Button { Content = "保存", Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 96 };
        saveButton.Click += (_, _) => Answer(keep: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(discardButton);
        buttons.Children.Add(saveButton);

        var root = new StackPanel { Padding = new Thickness(24, 20, 24, 20), Spacing = 10 };
        root.Children.Add(title);
        root.Children.Add(question);
        if (system.Recorded)
        {
            root.Children.Add(_system);
        }

        if (microphone.Recorded)
        {
            root.Children.Add(_microphone);
        }

        root.Children.Add(hint);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(buttons);
        root.KeyDown += OnKeyDown;
        Content = root;
        Glass.Attach(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        Closed += (_, _) => Answer(keep: true);
        WindowChrome.CenterOnCurrentMonitor(this, 420, 300);
    }

    public void ShowAndActivate()
    {
        Activate();
        WindowChrome.ActivateForeground(this);
    }

    private static CheckBox Choice(string name, Source source) => new()
    {
        Content = source.Silent ? $"{name}(全程没有声音)" : name,
        IsChecked = source.Recorded && !source.Silent,
    };

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Escape)
        {
            e.Handled = true;
            Answer(keep: true);
        }
    }

    private void Answer(bool keep)
    {
        if (_answered)
        {
            return;
        }

        _answered = true;
        if (keep)
        {
            _save(_system.IsChecked == true, _microphone.IsChecked == true);
        }
        else
        {
            _discard();
        }

        Close();
    }
}
