using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Infrastructure;
using Windows.System;

namespace Waio.Feature.Launcher.UI;

/// <summary>面板的输入采集(键盘交给 LauncherKeys 判定、输入法组字、鼠标、右键菜单)和应用图标加载。</summary>
internal sealed partial class LauncherWindow
{
    /// <summary>输入法正在处理按键时系统报告的虚拟键码 VK_PROCESSKEY(0xE5);WinUI 的 VirtualKey 枚举里没有这个成员。</summary>
    private const VirtualKey ImeProcessKey = (VirtualKey)229;

    /// <summary>输入法"上屏"的那个回车可能在组字结束事件之后才到;组字结束后再保持这么久"仍在组字"(同剪贴板面板)。</summary>
    private static readonly TimeSpan CompositionGrace = TimeSpan.FromMilliseconds(80);

    /// <summary>图标显示大小(逻辑像素),按显示器缩放换算成要多大的位图。</summary>
    private const int IconDip = 32;

    /// <summary>缓存上限:超过就整个清掉重来(应用一般几百个,正常用不到)。</summary>
    private const int IconCacheLimit = 600;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _compositionTimer;
    private readonly Func<string, int, Task<IconPixels?>> _loadIcon;
    private readonly Dictionary<string, ImageSource> _icons = [];
    private readonly HashSet<string> _loadingIcons = [];
    private readonly HashSet<string> _noIcon = []; // 拿不到图标的,不再反复去要
    private bool _composing;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateCompositionTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = CompositionGrace;
        timer.Tick += (_, _) => _composing = false;
        return timer;
    }

    // 根元素的 PreviewKeyDown 是"隧道"事件:比搜索框自己的处理更早拿到按键
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_presenter is null || e.Key == ImeProcessKey)
        {
            return; // 输入法正在处理这个键
        }

        var input = MapKey(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu));
        var context = new LauncherKeyContext(_composing, SearchBox.SelectionLength > 0, SearchBox.Text.Length == 0);
        var action = _presenter.Resolve(input, context);
        if (action.Kind == LauncherActionKind.None)
        {
            return; // 交给搜索框(打字、左右移动光标、Ctrl+A、Ctrl+V……)
        }

        e.Handled = true;
        Fire(() => _presenter.ExecuteAsync(action));
    }

    private static LauncherKeyInput MapKey(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return new LauncherKeyInput(LauncherKey.Digit, ctrl, shift, alt, key - VirtualKey.Number0);
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            return new LauncherKeyInput(LauncherKey.Digit, ctrl, shift, alt, key - VirtualKey.NumberPad0);
        }

        var mapped = key switch
        {
            VirtualKey.Up => LauncherKey.Up,
            VirtualKey.Down => LauncherKey.Down,
            VirtualKey.Enter => LauncherKey.Enter,
            VirtualKey.Escape => LauncherKey.Escape,
            VirtualKey.Tab => LauncherKey.Tab,
            VirtualKey.Back => LauncherKey.Backspace,
            VirtualKey.C => LauncherKey.C,
            VirtualKey.N => LauncherKey.N,
            VirtualKey.P => LauncherKey.P,
            _ => LauncherKey.Other,
        };
        return new LauncherKeyInput(mapped, ctrl, shift, alt);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => _presenter?.OnQueryChanged(SearchBox.Text);

    private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _compositionTimer.Stop();
        _composing = true;
    }

    private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _compositionTimer.Stop();
        _compositionTimer.Start(); // 宽限期过后才算组字结束
    }

    // ---- 鼠标:悬停高亮,右键菜单(单击 / 双击 / 拖动在 .Pointer.cs) ----

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            row.IsHovered = true;
        }
    }

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            row.IsHovered = false;
        }
    }

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
    {
        if (_presenter is not null)
        {
            Fire(_presenter.OpenSettingsAsync);
        }
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e) => WithRow(sender, LauncherActionKind.Open);

    private void OnMenuReveal(object sender, RoutedEventArgs e) => WithRow(sender, LauncherActionKind.Reveal);

    private void OnMenuCopy(object sender, RoutedEventArgs e) => WithRow(sender, LauncherActionKind.Copy);

    private void WithRow(object sender, LauncherActionKind kind)
    {
        if (_presenter is not null && (sender as FrameworkElement)?.Tag is LauncherRow { Item: not null } row)
        {
            _presenter.State.Select(row.ItemIndex);
            Fire(() => _presenter.ExecuteAsync(new LauncherAction(kind)));
        }
    }

    // ---- 图标:行真正要显示时才加载(ItemsRepeater 虚拟化),加载过的缓存 ----
    // 应用按应用缓存;文件按扩展名缓存(同一种文件图标一样,省得几千个文件各取一次),
    // 自带图标的(exe、lnk、ico、url)按路径;文件夹共用一个。

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index >= 0 && args.Index < Rows.Count && Rows[args.Index] is { Item: { HasShellIcon: true } item, Icon: null })
        {
            _ = LoadIconAsync(item);
        }
    }

    private void ApplyCachedIcon(LauncherRow row)
    {
        if (row.Item is { HasShellIcon: true } item && _icons.TryGetValue(IconKey(item), out var cached))
        {
            row.Icon = cached;
        }
    }

    private static string IconKey(LauncherItem item)
    {
        if (item.Kind == LauncherItemKind.App)
        {
            return item.Id;
        }

        if (item.Kind == LauncherItemKind.Folder)
        {
            return "folder";
        }

        var extension = System.IO.Path.GetExtension(item.Target).ToLowerInvariant();
        return extension is ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms" or "" ? item.Id : "ext:" + extension;
    }

    private async Task LoadIconAsync(LauncherItem item)
    {
        var key = IconKey(item);
        if (_noIcon.Contains(key) || !_loadingIcons.Add(key))
        {
            return;
        }

        try
        {
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var size = (int)Math.Round(IconDip * scale);
            if (await _loadIcon(item.Target, size) is not { } pixels)
            {
                _noIcon.Add(key);
                return; // 拿不到:保留字形图标
            }

            var bitmap = await ToBitmapAsync(pixels);
            if (_icons.Count >= IconCacheLimit)
            {
                _icons.Clear();
            }

            _icons[key] = bitmap;
            foreach (var row in Rows)
            {
                if (row.Item is { HasShellIcon: true } shown && IconKey(shown) == key)
                {
                    row.Icon = bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "启动台图标加载失败:{Title}", item.Title);
        }
        finally
        {
            _loadingIcons.Remove(key);
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
            _logger.LogError(ex, "启动台操作失败");
        }
    }
}
