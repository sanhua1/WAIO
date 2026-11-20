using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Kit.Commands;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>设置页的贴图修饰键、打开截图历史、手动清理(MAIO pinRows / historyRows / HistoryStorageView)。</summary>
internal sealed partial class ScreenshotSettingsView
{
    private static readonly (PinModifier, string)[] Modifiers = [.. Enum.GetValues<PinModifier>().Select(m => (m, m.Title()))];

    private TextBlock _modifierWarning = NewModifierWarning();

    private static TextBlock NewModifierWarning() => new()
    {
        Text = "⚠ 两个修饰键相同时,只能调整缩放", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
        Margin = new Thickness(16, 0, 0, 0),
    };

    private HistoryStore Store => HistoryStore.For(_context.Storage.DataDirectory, _context.Logger);

    /// <summary>保存修饰键,两个相同时显示提示。</summary>
    private void SaveModifier(Action<ScreenshotSettings> change)
    {
        Save(change);
        UpdateModifierWarning();
    }

    private void UpdateModifierWarning() =>
        _modifierWarning.Visibility = Value.PinZoomModifier == Value.PinOpacityModifier ? Visibility.Visible : Visibility.Collapsed;

    private Button OpenHistoryButton() => SettingsUi.Button("打开截图历史…", () => _ = OpenHistoryAsync());

    private async Task OpenHistoryAsync()
    {
        try
        {
            await _context.Commands.PerformAsync(ScreenshotFeature.History, CommandArguments.Empty);
        }
        catch (CommandException ex)
        {
            _context.Logger.LogWarning(ex, "打开截图历史失败");
            await new ContentDialog
            {
                Title = "无法打开", Content = "截图功能没有启用。先在上方打开它再试。", CloseButtonText = "知道了", XamlRoot = _host.XamlRoot,
            }.ShowAsync();
        }
    }

    /// <summary>手动清理:删除最旧的 N MB、删除某日期之前的、清空(收藏的不删,MAIO HistoryStorageView)。</summary>
    private Border CleanupCard()
    {
        UpdateModifierWarning();
        var megabytes = new NumberBox { Value = 200, Minimum = 1, Maximum = 1_000_000, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var cutoff = new CalendarDatePicker { Date = DateTimeOffset.Now.AddMonths(-3) };
        var row = new StackPanel { Spacing = 8 };

        var oldest = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        oldest.Children.Add(new TextBlock { Text = "删除最旧的", VerticalAlignment = VerticalAlignment.Center });
        oldest.Children.Add(megabytes);
        oldest.Children.Add(new TextBlock { Text = "MB", VerticalAlignment = VerticalAlignment.Center });
        oldest.Children.Add(SettingsUi.Button("删除…", () =>
        {
            var size = Math.Max(1, double.IsNaN(megabytes.Value) ? 1 : (long)megabytes.Value);
            _ = CleanupAsync($"删除最旧的 {size} MB 截图?", records => HistoryRetention.Oldest(records, size * 1_000_000));
        }));

        var before = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        before.Children.Add(new TextBlock { Text = "删除此日期之前的", VerticalAlignment = VerticalAlignment.Center });
        before.Children.Add(cutoff);
        before.Children.Add(SettingsUi.Button("删除…", () =>
        {
            var date = (cutoff.Date ?? DateTimeOffset.Now).Date;
            _ = CleanupAsync($"删除 {date:yyyy年M月d日} 之前的截图?", records => HistoryRetention.Before(records, new DateTimeOffset(date)));
        }));

        var all = SettingsUi.Button("清空截图历史…", () =>
            _ = CleanupAsync("清空全部截图历史?", records => [.. records.Where(r => !r.Pinned).Select(r => r.Id)]));

        row.Children.Add(oldest);
        row.Children.Add(before);
        row.Children.Add(all);
        return SettingsUi.Card("手动清理", "收藏的截图不会被删除。删除后无法恢复。", row);
    }

    private async Task CleanupAsync(string title, Func<IReadOnlyList<ScreenshotRecord>, IReadOnlyList<string>> select)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = "收藏的截图不会被删除。此操作无法撤销。", PrimaryButtonText = "删除", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, XamlRoot = _host.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var store = Store;
            var ids = select(await store.RecordsAsync());
            await store.DeleteAsync(ids);
            _usage.Text = $"已删除 {ids.Count} 张";
            await Task.Delay(1500);
            await LoadUsageAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Logger.LogWarning(ex, "清理截图历史失败");
            _usage.Text = $"清理失败:{ex.Message}";
        }
    }
}
