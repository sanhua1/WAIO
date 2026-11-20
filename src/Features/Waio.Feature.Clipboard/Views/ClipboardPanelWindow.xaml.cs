using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.Clipboard.Domain;
using Waio.Feature.Clipboard.Panel;
using Waio.Kit.Services;
using Waio.UI;
using Waio.UI.Theming;
using Windows.Graphics;

namespace Waio.Feature.Clipboard.Views;

/// <summary>
/// 剪贴板面板窗口:纯弹出式(无标题栏/边框)、圆角、置顶、毛玻璃,可获得焦点(要在搜索框里打字)。
/// 只做显示和输入采集;所有"该怎么办"都交给 <see cref="PanelPresenter"/>,键位表在 <see cref="PanelKeys"/>。
/// 按职责拆成四个文件:本文件(显示)、<c>.Input.cs</c>(键盘鼠标)、<c>.Images.cs</c>(缩略图与图片预览)、
/// <c>.Dismiss.cs</c>(点到别处自动关闭)。
/// </summary>
internal sealed partial class ClipboardPanelWindow : Window, IPanelView
{
    private const double WidthDip = 360;
    private const double HeightDip = 440;
    private const int PreviewMaxChars = 20_000;

    private readonly ILogger _logger;
    private PanelPresenter? _presenter;
    private int _renderedVersion = -1;
    private ClipRow? _selectedRow;
    private Control? _focusBeforePreview;

    public ClipboardPanelWindow(ILogger logger)
    {
        _logger = logger;
        InitializeComponent();
        WindowChrome.MakeOverlay(this, activatable: true);
        WindowChrome.MakePopupSurface(this, dark: Glass.IsDark());
        Glass.Attach(this, popup: true); // 磨砂玻璃,参数跟随外观设置(ADR-0026)
        Activated += OnActivated;
        _compositionTimer = CreateCompositionTimer();
        _dismissTimer = CreateDismissTimer();
    }

    public ObservableCollection<ClipRow> Rows { get; } = [];

    public bool IsVisible => AppWindow.IsVisible;

    public bool IsActive => IsVisible && WindowChrome.IsForeground(this);

    public void Attach(PanelPresenter presenter) => _presenter = presenter;

    // ---- IPanelView ----

    public void Render(PickerState state)
    {
        if (state.Version != _renderedVersion)
        {
            Rows.Clear();
            _selectedRow = null;
            var now = DateTimeOffset.UtcNow;
            foreach (var item in state.Items)
            {
                var row = new ClipRow(item, state.QuickSlotOf(item), now);
                ApplyCachedThumbnail(row);
                Rows.Add(row);
            }

            _renderedVersion = state.Version;
            var empty = Rows.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyMessage.Text = SearchBox.Text.Length > 0 ? "没有匹配的记录" : "还没有记录";
            StatusText.Text = empty ? "" : $"{Rows.Count} 条";
        }

        Select(state.SelectedIndex);
    }

    public void ShowPreview(ClipItem item, ClipboardContent? content)
    {
        if (PreviewHost.Visibility != Visibility.Visible)
        {
            // 先把焦点交给预览区再隐藏列表:有焦点的元素被隐藏时,WinUI 会把焦点挪到下一个可聚焦的控件
            // (搜索框),之后的空格就会打进搜索框。关闭预览时还给原来的位置。
            _focusBeforePreview = SearchBox.FocusState != FocusState.Unfocused ? SearchBox : Scroller;
            PreviewHost.Visibility = Visibility.Visible;
            PreviewHost.Focus(FocusState.Programmatic);
        }

        Scroller.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewImageFrame.Visibility = Visibility.Collapsed;
        PreviewHost.ChangeView(null, 0, null, disableAnimation: true);

        if (content is null)
        {
            PreviewText.Text = "加载中…";
            PreviewText.Visibility = Visibility.Visible;
            return;
        }

        var text = content.Files.Count > 0 ? string.Join(Environment.NewLine, content.Files) : content.Text ?? "";
        PreviewText.Text = text.Length > PreviewMaxChars ? text[..PreviewMaxChars] + "…" : text;
        PreviewText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = DescribeContent(content);

        // 有图片就显示(包括 PixPin 这类同时给出文件路径和图片的复制)
        if (content.Image is { Length: > 0 })
        {
            _ = ShowPreviewImageAsync(item.Id, content.Image);
        }
    }

    public void ClosePreview()
    {
        var wasOpen = PreviewHost.Visibility == Visibility.Visible;
        Scroller.Visibility = Visibility.Visible;
        if (wasOpen)
        {
            (_focusBeforePreview ?? Scroller).Focus(FocusState.Programmatic); // 回到预览前的焦点(通常是列表)
            _focusBeforePreview = null;
        }

        PreviewHost.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        StatusText.Text = Rows.Count == 0 ? "" : $"{Rows.Count} 条";
    }

    public void SetQuery(string text)
    {
        if (SearchBox.Text != text)
        {
            SearchBox.Text = text;
        }
    }

    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    public void FocusList() => Scroller.Focus(FocusState.Programmatic);

    public void Show(PanelPlacement placement, bool focusSearch)
    {
        var width = (int)(WidthDip * placement.Scale);
        var height = (int)(HeightDip * placement.Scale);
        var topLeft = placement.Anchor is { } anchor
            ? PopupPlacement.TopLeft(anchor, width, height, placement.WorkArea)
            : PopupPlacement.Centered(width, height, placement.WorkArea);

        var previous = WindowChrome.Foreground();
        AppWindow.MoveAndResize(new RectInt32(topLeft.X, topLeft.Y, width, height));
        if (!WindowChrome.ActivateForeground(this))
        {
            // 没抢到前台:面板看得到但按键进不来。再按一次快捷键会重新弹出(见 PanelPresenter.ToggleAsync)
            _logger.LogWarning("剪贴板面板没能成为前台窗口");
        }

        (focusSearch ? (Control)SearchBox : Scroller).Focus(FocusState.Programmatic);
        StartDismissWatch(previous);
    }

    public void Hide()
    {
        StopDismissWatch();
        AppWindow.Hide();
    }

    // ---- 内部 ----

    /// <summary>高亮选中行并滚到看得见的位置。</summary>
    private void Select(int index)
    {
        var row = index >= 0 && index < Rows.Count ? Rows[index] : null;
        if (!ReferenceEquals(row, _selectedRow))
        {
            if (_selectedRow is not null)
            {
                _selectedRow.IsSelected = false;
            }

            _selectedRow = row;
            if (row is not null)
            {
                row.IsSelected = true;
            }
        }

        if (row is null)
        {
            return;
        }

        if (index == 0)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }

        var element = Repeater.GetOrCreateElement(index);
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void OnSearchFocusChanged(bool focused)
    {
        SearchKeyHint.Visibility = focused ? Visibility.Collapsed : Visibility.Visible;
        _presenter?.SetSearchFocused(focused);
    }

    private static string DescribeContent(ClipboardContent content)
    {
        var parts = new List<string>();
        if (content.Image is { Length: > 0 } && Infrastructure.DibImage.SizeOf(content.Image) is var (w, h))
        {
            parts.Add($"{w} × {h}");
        }

        if (!string.IsNullOrEmpty(content.Text))
        {
            parts.Add($"{content.Text.Length:N0} 字");
        }

        if (content.Files.Count > 0)
        {
            parts.Add($"{content.Files.Count} 个文件");
        }

        if (content.Rtf is { Length: > 0 } || content.Html is { Length: > 0 })
        {
            parts.Add("带格式");
        }

        return string.Join(" · ", parts);
    }
}
