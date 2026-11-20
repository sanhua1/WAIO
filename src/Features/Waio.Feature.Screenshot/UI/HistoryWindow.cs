using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.UI;
using Waio.UI.Theming;
using Windows.System;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 截图历史面板(MAIO HistoryBrowser,spec F11、4.7):按日期分组的缩略图网格,新的在前。
/// 双击 / ⏎ 贴图,Ctrl+C 复制,E 重新编辑标注,Delete / Backspace 删除;右键菜单还有识别文字、翻译、收藏、打开所在文件夹。
/// 搜索按日期(2026-11-03)、来源程序、识别出的文字(只有识别过的截图才有文字,不会自动识别每一张)。网格是虚拟化的,缩略图滚到时才加载。只在 UI 线程上使用。
/// </summary>
internal sealed class HistoryWindow : Window
{
    private const string ItemTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Width="200" Height="156" Padding="6" RowSpacing="4">
            <Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
            <Border CornerRadius="4" Background="{ThemeResource SubtleFillColorSecondaryBrush}">
              <Image Stretch="Uniform"/>
            </Border>
            <FontIcon Glyph="&#xE735;" FontSize="14" Foreground="Gold" Margin="4" HorizontalAlignment="Right" VerticalAlignment="Top"/>
            <TextBlock Grid.Row="1" FontSize="12" Opacity="0.7" TextTrimming="CharacterEllipsis"/>
          </Grid>
        </DataTemplate>
        """;

    /// <summary>面板上的操作(由控制器实现;参数是记录 id)。</summary>
    internal sealed record Actions(
        Action<string> Pin, Action<string> Copy, Action<string> Edit, Action<string> Reveal, Action<string, bool> Recognize);

    /// <summary>一天的记录;ToString 是分组标题(分组标题默认显示它)。</summary>
    private sealed class DayGroup(DateTime day, IEnumerable<ScreenshotRecord> records) : List<ScreenshotRecord>(records)
    {
        public override string ToString() => day.ToString("yyyy年M月d日 dddd");
    }

    private readonly HistoryStore _store;
    private readonly Actions _actions;
    private readonly GridView _grid = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(12, 0, 12, 12) };
    private readonly CollectionViewSource _source = new() { IsSourceGrouped = true };
    private readonly TextBox _search = new() { PlaceholderText = "搜索:日期(2026-11-03)、来源程序、识别出的文字", Width = 360 };
    private readonly TextBlock _stats = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Opacity = 0.7 };
    private readonly MenuFlyout _menu = new();
    private IReadOnlyList<ScreenshotRecord> _records = [];
    private bool _reloadQueued;

    public HistoryWindow(HistoryStore store, Actions actions)
    {
        _store = store;
        _actions = actions;
        Title = "截图历史";
        _grid.ItemTemplate = (DataTemplate)XamlReader.Load(ItemTemplate);
        _grid.GroupStyle.Add(new GroupStyle());
        _grid.ContainerContentChanging += OnContainerContentChanging;
        _grid.DoubleTapped += (_, e) => Run(_actions.Pin, RecordOf(e.OriginalSource));
        _grid.KeyDown += OnKeyDown;
        _search.TextChanged += (_, _) => Refresh();
        BuildMenu();

        var header = new Grid { Padding = new Thickness(12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(_search);
        Grid.SetColumn(_stats, 1);
        _stats.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(_stats);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(_grid, 1);
        Grid.SetRow(_empty, 1);
        root.Children.Add(_grid);
        root.Children.Add(_empty);
        Content = root;
        Glass.Attach(this); // 磨砂玻璃,参数跟随外观设置(ADR-0026)

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;
        WindowChrome.CenterOnCurrentMonitor(this, 900, 640);
    }

    /// <summary>显示并带到前台,重新读取记录(MAIO show)。</summary>
    public void ShowAndActivate()
    {
        Activate();
        WindowChrome.ActivateForeground(this);
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _records = await _store.RecordsAsync();
        var (count, bytes, pinned) = await _store.StatsAsync();
        _stats.Text = $"{count} 张 · {bytes / 1_000_000.0:0.#} MB" + (pinned > 0 ? $" · 收藏 {pinned} 张" : "");
        Refresh();
    }

    private void Refresh()
    {
        var groups = HistorySearch.GroupByDay(HistorySearch.Filter(_records, _search.Text));
        _source.Source = groups.Select(g => new DayGroup(g.Day, g.Records)).ToList();
        _grid.ItemsSource = _source.View;
        var empty = groups.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _records.Count == 0 ? "还没有截图\n按 Win+C 截图后会出现在这里" : "没有找到\n可以按日期(2026-11-03)、来源程序或识别出的文字搜索";
    }

    /// <summary>历史变化可能来自任意线程:合并成一次,回到 UI 线程重新读取。</summary>
    private void OnStoreChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_reloadQueued)
            {
                return;
            }

            _reloadQueued = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _reloadQueued = false;
                _ = ReloadAsync();
            });
        });
    }

    /// <summary>格子滚进视野时才填内容、加载缩略图(按需解码,省内存)。</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ScreenshotRecord record || args.ItemContainer.ContentTemplateRoot is not Grid cell)
        {
            return;
        }

        args.ItemContainer.ContextFlyout = _menu;
        var image = (Image)((Border)cell.Children[0]).Child;
        var star = (FontIcon)cell.Children[1];
        var caption = (TextBlock)cell.Children[2];
        caption.Text = $"{record.CreatedAt.ToLocalTime():HH:mm} · {record.PixelWidth}×{record.PixelHeight}"
            + (record.SourceApp is { } app ? $" · {app}" : "");
        star.Visibility = record.Pinned ? Visibility.Visible : Visibility.Collapsed;
        image.Source = args.InRecycleQueue ? null : new BitmapImage
        {
            UriSource = new Uri(_store.ThumbnailPath(record.Id)), DecodePixelWidth = 376, DecodePixelType = DecodePixelType.Logical,
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
        };
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_grid.SelectedItem is not ScreenshotRecord record)
        {
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                _actions.Pin(record.Id);
                break;
            case VirtualKey.Delete or VirtualKey.Back:
                _ = _store.DeleteAsync([record.Id]);
                break;
            case VirtualKey.E when !ctrl:
                _actions.Edit(record.Id);
                break;
            case VirtualKey.C when ctrl:
                _actions.Copy(record.Id);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void BuildMenu()
    {
        ScreenshotRecord? target = null;
        MenuFlyoutItem Item(string text, Action<ScreenshotRecord> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                if (target is { } record)
                {
                    action(record);
                }
            };
            return item;
        }

        var favorite = Item("", r => _ = _store.SetPinnedAsync(r.Id, !r.Pinned));
        _menu.Items.Add(Item("贴图", r => _actions.Pin(r.Id)));
        _menu.Items.Add(Item("复制", r => _actions.Copy(r.Id)));
        _menu.Items.Add(Item("编辑标注", r => _actions.Edit(r.Id)));
        _menu.Items.Add(Item("识别文字", r => _actions.Recognize(r.Id, false)));
        _menu.Items.Add(Item("翻译", r => _actions.Recognize(r.Id, true)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(favorite);
        _menu.Items.Add(Item("打开所在文件夹", r => _actions.Reveal(r.Id)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(Item("删除", r => _ = _store.DeleteAsync([r.Id])));
        _menu.Opening += (_, _) =>
        {
            target = (_menu.Target as SelectorItem)?.Content as ScreenshotRecord;
            if (target is not null)
            {
                _grid.SelectedItem = target;
                favorite.Text = target.Pinned ? "取消收藏" : "收藏(不会被自动清理)";
            }
        };
    }

    private static ScreenshotRecord? RecordOf(object source) => (source as FrameworkElement)?.DataContext as ScreenshotRecord;

    private static void Run(Action<string> action, ScreenshotRecord? record)
    {
        if (record is not null)
        {
            action(record.Id);
        }
    }
}
