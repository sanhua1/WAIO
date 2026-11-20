using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Waio.Feature.Clipboard.Domain;
using Windows.System;

namespace Waio.Feature.Clipboard.Views;

/// <summary>面板的输入采集:键盘(交给 PanelKeys 判定)、搜索框、输入法组字、鼠标与右键菜单。</summary>
internal sealed partial class ClipboardPanelWindow
{
    /// <summary>输入法正在处理按键时,系统报告的虚拟键码 VK_PROCESSKEY(0xE5);WinUI 的 VirtualKey 枚举里没有这个成员。</summary>
    private const VirtualKey ImeProcessKey = (VirtualKey)229;

    /// <summary>
    /// 输入法"上屏"的那个回车,可能在 TextCompositionEnded 之后才作为按键到达;组字结束后再保持
    /// 这么久"仍视为组字",避免它被当成"粘贴"。
    /// </summary>
    private static readonly TimeSpan CompositionGrace = TimeSpan.FromMilliseconds(80);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _compositionTimer;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateCompositionTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = CompositionGrace;
        timer.Tick += (_, _) => _presenter?.SetComposing(false);
        return timer;
    }

    // 根元素的 PreviewKeyDown/Up 是"隧道"事件:比搜索框、列表自己的处理更早拿到按键
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_presenter is null || e.Key == ImeProcessKey)
        {
            return; // 输入法正在处理这个键
        }

        var input = MapKey(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), e.KeyStatus.WasKeyDown);
        var action = _presenter.Resolve(input);
        if (action.Kind == PanelActionKind.None)
        {
            return; // 交给文本框/列表的默认行为(打字、Ctrl+C 等)
        }

        e.Handled = true;
        Fire(() => _presenter.ExecuteAsync(action));
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_presenter is null || e.Key == ImeProcessKey)
        {
            return;
        }

        var action = _presenter.ResolveKeyUp(MapKey(e.Key, ctrl: false, shift: false, repeat: false));
        if (action.Kind != PanelActionKind.None)
        {
            e.Handled = true;
            Fire(() => _presenter.ExecuteAsync(action));
        }
    }

    private static PanelKeyInput MapKey(VirtualKey key, bool ctrl, bool shift, bool repeat)
    {
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return new PanelKeyInput(PanelKey.Digit, ctrl, shift, key - VirtualKey.Number0, repeat);
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            return new PanelKeyInput(PanelKey.Digit, ctrl, shift, key - VirtualKey.NumberPad0, repeat);
        }

        var mapped = key switch
        {
            VirtualKey.Up => PanelKey.Up,
            VirtualKey.Down => PanelKey.Down,
            VirtualKey.Enter => PanelKey.Enter,
            VirtualKey.Escape => PanelKey.Escape,
            VirtualKey.Space => PanelKey.Space,
            VirtualKey.Back => PanelKey.Backspace,
            VirtualKey.Delete => PanelKey.Delete,
            VirtualKey.P => PanelKey.P,
            VirtualKey.F => PanelKey.F,
            VirtualKey.W => PanelKey.W,
            _ => PanelKey.Other,
        };
        return new PanelKeyInput(mapped, ctrl, shift, Repeat: repeat);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnQueryChanged(object sender, TextChangedEventArgs e) =>
        Fire(() => _presenter?.OnQueryChangedAsync(SearchBox.Text) ?? Task.CompletedTask);

    private void OnSearchGotFocus(object sender, RoutedEventArgs e) => OnSearchFocusChanged(true);

    private void OnSearchLostFocus(object sender, RoutedEventArgs e) => OnSearchFocusChanged(false);

    private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _compositionTimer.Stop();
        _presenter?.SetComposing(true);
    }

    private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _compositionTimer.Stop();
        _compositionTimer.Start(); // 宽限期过后才算组字结束
    }

    // ---- 鼠标:单击粘贴,悬停高亮,右键菜单在 XAML 里 ----

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipRow row && _presenter is not null)
        {
            e.Handled = true;
            Fire(() => _presenter.PasteAsync(row.Item, plain: false));
        }
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipRow row)
        {
            row.IsHovered = true;
        }
    }

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipRow row)
        {
            row.IsHovered = false;
        }
    }

    private void OnMenuPaste(object sender, RoutedEventArgs e) => WithRow(sender, row => _presenter!.PasteAsync(row.Item, plain: false));

    private void OnMenuPastePlain(object sender, RoutedEventArgs e) => WithRow(sender, row => _presenter!.PasteAsync(row.Item, plain: true));

    private void OnMenuCopy(object sender, RoutedEventArgs e) => WithRow(sender, row => _presenter!.CopyOnlyAsync(row.Item));

    private void OnMenuPreview(object sender, RoutedEventArgs e) => WithRow(sender, row => _presenter!.PreviewAsync(row.Item));

    private void OnMenuPin(object sender, RoutedEventArgs e) =>
        WithRow(sender, row => _presenter!.SetPinnedAsync(row.Item.Id, !row.Item.IsPinned));

    private void OnMenuDelete(object sender, RoutedEventArgs e) => WithRow(sender, row => _presenter!.DeleteAsync(row.Item.Id));

    private void WithRow(object sender, Func<ClipRow, Task> work)
    {
        if (_presenter is not null && (sender as FrameworkElement)?.Tag is ClipRow row)
        {
            Fire(() => work(row));
        }
    }

    /// <summary>事件处理器里不能 await,又不能让异常变成无人观察的崩溃:统一在这里记日志。</summary>
    private async void Fire(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "剪贴板面板操作失败");
        }
    }
}
