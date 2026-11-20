using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Infrastructure;
using Waio.Feature.Launcher.Panel;
using Waio.UI;
using Waio.UI.Theming;
using Windows.Graphics;

namespace Waio.Feature.Launcher.UI;

/// <summary>
/// 启动台面板:纯弹出式(无标题栏 / 边框)、圆角、置顶、毛玻璃,可获得焦点(要在搜索框里打字,中文输入法要能用)。
/// 只做显示和输入采集;"该怎么办"都交给 <see cref="LauncherPresenter"/>,键位表在 <see cref="LauncherKeys"/>。
/// 拆成两个文件:本文件(显示、定位),<c>.Input.cs</c>(键盘、鼠标、输入法、图标加载)。
/// </summary>
internal sealed partial class LauncherWindow : Window, ILauncherView
{
    /// <summary>搜索框那一行的高度(XAML 第一行),定位时让它的中线落在黄金分割处。</summary>
    private const double SearchRowDip = 56;

    private readonly ILogger _logger;
    private readonly PopupDismissWatcher _dismiss;
    private LauncherPresenter? _presenter;
    private int _renderedVersion = -1;
    private LauncherRow? _selectedRow;
    private readonly List<int> _rowOfItem = [];

    public LauncherWindow(ILogger logger, Func<string, int, Task<IconPixels?>> loadIcon)
    {
        _logger = logger;
        _loadIcon = loadIcon;
        InitializeComponent();
        WindowChrome.MakeOverlay(this, activatable: true);
        WindowChrome.MakePopupSurface(this, dark: Glass.IsDark());
        Glass.Attach(this, popup: true); // 磨砂玻璃,参数跟随外观设置(ADR-0026)
        _compositionTimer = CreateCompositionTimer();
        _dismiss = new PopupDismissWatcher(this, () =>
        {
            _logger.LogInformation("启动台:前台已切到别的窗口,关闭");
            Fire(() => _presenter?.CloseAsync(restoreFocus: false) ?? Task.CompletedTask);
        });
    }

    public ObservableCollection<LauncherRow> Rows { get; } = [];

    public bool IsVisible => AppWindow.IsVisible;

    public bool IsActive => IsVisible && WindowChrome.IsForeground(this);

    public void Attach(LauncherPresenter presenter) => _presenter = presenter;

    // ---- ILauncherView ----

    public void Render(LauncherState state)
    {
        if (state.Version != _renderedVersion)
        {
            RebuildRows(state);
            _renderedVersion = state.Version;
        }

        if (Tabs.Children.Count != state.Sources.Count)
        {
            BuildTabs(state.Sources);
        }

        UpdateTabs(state.EffectiveSource);
        TabStatus.Text = StatusText(state);
        Select(state.SelectedIndex);
        UpdatePreview(state.Selected);
    }

    public void SetQuery(string text)
    {
        if (SearchBox.Text != text)
        {
            SearchBox.Text = text;
            SearchBox.SelectionStart = text.Length;
        }
    }

    public void Show(PanelPlacement placement)
    {
        var bounds = PanelGeometry.Bounds(placement, SearchRowDip);
        SetPreviewVisible(PanelGeometry.ShowsPreview(bounds.Width / placement.Scale));

        var previous = WindowChrome.Foreground();
        // 先挪到目标显示器上再定尺寸:跨不同缩放的显示器时系统会按新 DPI 自动缩放一次
        AppWindow.Move(new PointInt32(bounds.Left, bounds.Top));
        AppWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        if (!WindowChrome.ActivateForeground(this))
        {
            _logger.LogWarning("启动台面板没能成为前台窗口");
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll(); // 保留了上次的输入时,直接打字就替换掉
        _dismiss.Start(previous);
    }

    public void Hide()
    {
        _dismiss.Stop();
        AppWindow.Hide();
        ClearPreview(); // 放掉预览的图片内存;下次打开重新读
    }

    // ---- 内部 ----

    private void RebuildRows(LauncherState state)
    {
        Rows.Clear();
        _rowOfItem.Clear();
        _selectedRow = null;
        var index = 0;
        foreach (var section in state.Sections)
        {
            Rows.Add(LauncherRow.Header(section.Title));
            foreach (var item in section.Items)
            {
                _rowOfItem.Add(Rows.Count);
                var compact = state.Settings.FileResultStyle == FileResultStyle.Compact && item.Kind is LauncherItemKind.File or LauncherItemKind.Folder or LauncherItemKind.MoreFiles;
                var row = LauncherRow.ForItem(item, index++, compact);
                ApplyCachedIcon(row);
                Rows.Add(row);
            }
        }

        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        var empty = state.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = state.Parsed.IsEmpty
            ? (state.Apps.Count == 0 ? "正在读取应用列表…" : state.Parsed.Source.IncludesFiles() && state.Sources.Count > 2 ? "输入应用名、文件名,拼音或首字母都行" : "输入应用名称、拼音或首字母")
            : state.Parsed.WantsFiles && state.FileStatus is { } status ? status : "没有找到";
    }

    /// <summary>高亮选中行并滚到看得见的位置。</summary>
    private void Select(int itemIndex)
    {
        var rowIndex = itemIndex >= 0 && itemIndex < _rowOfItem.Count ? _rowOfItem[itemIndex] : -1;
        var row = rowIndex >= 0 ? Rows[rowIndex] : null;
        if (!ReferenceEquals(row, _selectedRow))
        {
            _selectedRow?.IsSelected = false;
            _selectedRow = row;
            row?.IsSelected = true;
        }

        if (row is null)
        {
            return;
        }

        if (itemIndex == 0)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true); // 第一项连同它的分组标题一起露出来
            return;
        }

        var element = Repeater.GetOrCreateElement(rowIndex);
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    /// <summary>标签栏右边:要搜文件、索引还没好时显示索引状态;否则显示应用数。</summary>
    private static string StatusText(LauncherState state)
    {
        if (state.Parsed.Source.IncludesFiles() && state.FileStatus is { } status)
        {
            return status;
        }

        return state.Apps.Count == 0 ? "" : $"{state.Apps.Count} 个应用";
    }

    private void BuildTabs(IReadOnlyList<LauncherSource> sources)
    {
        Tabs.Children.Clear();
        foreach (var source in sources)
        {
            var text = new TextBlock { Text = source.Title(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var pill = new Border
            {
                Tag = source,
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(12, 3, 12, 4),
                Child = text,
            };
            pill.Tapped += (_, e) =>
            {
                e.Handled = true;
                Fire(() => _presenter?.SelectSourceAsync(source) ?? Task.CompletedTask);
            };
            Tabs.Children.Add(pill);
        }
    }

    private void UpdateTabs(LauncherSource active)
    {
        foreach (var child in Tabs.Children)
        {
            if (child is Border { Tag: LauncherSource source, Child: TextBlock text } pill)
            {
                var on = source == active;
                pill.Background = on ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] : null;
                text.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                text.Foreground = (Brush)Application.Current.Resources[on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
            }
        }
    }
}
