using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Translate.Domain;
using Waio.Feature.Translate.Infrastructure;
using Windows.ApplicationModel.DataTransfer;

namespace Waio.Feature.Translate.Views;

/// <summary>翻译历史(MAIO <c>HistoryView</c>):搜索、复制、删除、清空。</summary>
internal static class HistoryDialog
{
    public static async Task ShowAsync(HistoryStore store, XamlRoot? root)
    {
        if (root is null)
        {
            return;
        }

        var search = new TextBox { PlaceholderText = "搜索原文或译文" };
        var clear = new Button { Content = "清空…" };
        var list = new StackPanel { Spacing = 6 };
        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(search);
        Grid.SetColumn(clear, 1);
        toolbar.Children.Add(clear);

        var body = new Grid { RowSpacing = 10, Width = 600, Height = 480 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.Children.Add(toolbar);
        var scroller = new ScrollViewer { Content = list };
        Grid.SetRow(scroller, 1);
        body.Children.Add(scroller);

        void Render()
        {
            var entries = store.Entries.Where(e => e.Matches(search.Text)).ToList();
            clear.IsEnabled = store.Entries.Count > 0;
            list.Children.Clear();
            if (entries.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = search.Text.Trim().Length == 0 ? "还没有翻译历史" : "没有找到", Margin = new Thickness(0, 40, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                });
                return;
            }

            foreach (var entry in entries)
            {
                list.Children.Add(Row(entry, () =>
                {
                    store.Remove(entry.Id);
                    Render();
                }));
            }
        }

        search.TextChanged += (_, _) => Render();
        var dialog = new ContentDialog { Title = "翻译历史", Content = body, CloseButtonText = "完成", XamlRoot = root };
        clear.Click += async (_, _) =>
        {
            // ContentDialog 里不能再弹 ContentDialog:用按钮上的确认浮层(MAIO confirmationDialog)
            var confirm = new Button { Content = "清空全部翻译历史" };
            var flyout = new Flyout { Content = confirm };
            confirm.Click += (_, _) =>
            {
                store.Clear();
                flyout.Hide();
                Render();
            };
            flyout.ShowAt(clear);
            await Task.CompletedTask;
        };
        Render();
        await dialog.ShowAsync();
    }

    private static Border Row(HistoryEntry entry, Action delete)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var panel = new StackPanel { Spacing = 4 };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = entry.Text, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true,
        });
        var date = new TextBlock { Text = entry.Date.ToLocalTime().ToString("M月d日 HH:mm"), FontSize = 12, Foreground = secondary };
        Grid.SetColumn(date, 1);
        head.Children.Add(date);
        panel.Children.Add(head);

        foreach (var item in entry.Results)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(new TextBlock { Text = item.Service, FontSize = 12, Foreground = secondary });
            var text = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            panel.Children.Add(line);
        }

        var menu = new MenuFlyout();
        menu.Items.Add(Item("复制原文", () => Copy(entry.Text)));
        foreach (var item in entry.Results)
        {
            menu.Items.Add(Item($"复制「{item.Service}」的译文", () => Copy(item.Text)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("删除", delete));

        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6), Child = panel, ContextFlyout = menu,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    private static MenuFlyoutItem Item(string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>复制到剪贴板(WinRT 剪贴板,只在设置窗口里用;会被剪贴板历史正常记录)。</summary>
    private static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
