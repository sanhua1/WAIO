using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waio.Kit.Input;

namespace Waio.UI;

public enum HotkeyRecordingOutcome
{
    Recorded,
    Cleared,
    Cancelled,

    /// <summary>录不了(输入引擎没运行)。</summary>
    Unavailable,
}

public readonly record struct HotkeyRecording(HotkeyRecordingOutcome Outcome, KeyChord Chord = default);

/// <summary>
/// 开始一次录制。<paramref name="hint"/> 可在任意线程调用;取消令牌触发时应返回 Cancelled。
/// 由宿主实现(真正的录制在输入引擎里,见 ADR-0011),UI 层不碰钩子。
/// </summary>
public delegate Task<HotkeyRecording> RecordHotkey(Action<string> hint, CancellationToken cancellationToken);

/// <summary>
/// 键帽样式的热键录制按钮(参考 MAIO 的 ShortcutRecorderButton):
/// 显示当前快捷键;点一下开始录,按下组合键即保存;单按 Backspace/Delete 清除,Esc 取消。
/// <para>
/// 录制期间输入引擎会吞掉全局按键,所以一定要能结束:再点一次、按钮失去焦点、离开页面、超时都会取消。
/// </para>
/// </summary>
public sealed class HotkeyRecorder
{
    /// <summary>超时自动取消:录制期间所有按键都被吞掉,不能让它无限期挂着。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly RecordHotkey _record;
    private readonly Action<KeyChord?> _changed;
    private const double MinButtonWidth = 96;

    private readonly Button _button = new() { MinWidth = MinButtonWidth, BorderThickness = new Thickness(1), MinHeight = 32, Padding = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock _hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _recording;
    private KeyChord? _chord;

    /// <param name="chord">当前快捷键;null 显示"录制快捷键"。</param>
    /// <param name="record">真正的录制实现。</param>
    /// <param name="changed">录到新快捷键(或清除时为 null)后回调,在 UI 线程上。</param>
    /// <param name="placeholder">没有快捷键时按钮上的文字。</param>
    public HotkeyRecorder(KeyChord? chord, RecordHotkey record, Action<KeyChord?> changed, string placeholder = "录制快捷键")
    {
        _chord = chord;
        _record = record;
        _changed = changed;
        Placeholder = placeholder;
        _hint.Foreground = Brush("SystemFillColorCautionBrush");

        _button.Click += (_, _) => Toggle();
        _button.LostFocus += (_, _) => Cancel();
        _button.Unloaded += (_, _) => Cancel();

        View = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Children = { _button, _hint } };
        ToolTipService.SetToolTip(_button, "点击后按下新的组合键;单按 Backspace 清除,Esc 取消");
        ShowChord();
    }

    public FrameworkElement View { get; }

    public string Placeholder { get; }

    public bool IsRecording => _recording is not null;

    /// <summary>外部改了快捷键(如手动选键)时同步显示,不触发回调。</summary>
    public void SetChord(KeyChord? chord)
    {
        _chord = chord;
        if (!IsRecording)
        {
            ShowChord();
        }
    }

    private void Toggle()
    {
        if (IsRecording)
        {
            Cancel();
            return;
        }

        _ = RecordAsync();
    }

    private void Cancel() => _recording?.Cancel();

    private async Task RecordAsync()
    {
        using var cts = new CancellationTokenSource(Timeout);
        _recording = cts;
        ShowRecording();
        var queue = _button.DispatcherQueue;
        try
        {
            var result = await _record(text => queue.TryEnqueue(() => ShowHint(text)), cts.Token);
            switch (result.Outcome)
            {
                case HotkeyRecordingOutcome.Recorded:
                    _chord = result.Chord;
                    _changed(result.Chord);
                    break;
                case HotkeyRecordingOutcome.Cleared when _chord is not null:
                    _chord = null;
                    _changed(null);
                    break;
                case HotkeyRecordingOutcome.Unavailable:
                    _recording = null;
                    ShowChord();
                    ShowHint("快捷键服务没有运行,无法录制,可以手动选择按键");
                    return;
            }
        }
        finally
        {
            _recording = null;
        }

        ShowChord();
    }

    private void ShowChord()
    {
        _hint.Visibility = Visibility.Collapsed;
        _button.MinWidth = MinButtonWidth;
        _button.BorderBrush = Brush("ControlElevationBorderBrush");
        if (_chord is not { } chord)
        {
            _button.Content = new TextBlock { Text = Placeholder, Foreground = Brush("TextFillColorSecondaryBrush") };
            return;
        }

        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var part in KeyChordText.Parts(chord))
        {
            caps.Children.Add(SettingsUi.KeyCap(part));
        }

        _button.Content = caps;
    }

    private void ShowRecording()
    {
        // 录制中按钮宽度不变(锁成当前宽度)、边框粗细不变:否则卡片右侧变宽变窄,左边说明换行,整页跟着上下跳
        _button.MinWidth = Math.Max(MinButtonWidth, _button.ActualWidth);
        _button.Content = new TextBlock { Text = "按下快捷键…", Foreground = Brush("AccentTextFillColorPrimaryBrush") };
        _button.BorderBrush = Brush("AccentFillColorDefaultBrush");
        _hint.Visibility = Visibility.Collapsed;
    }

    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.Visibility = Visibility.Visible;
    }

    private static Brush? Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
