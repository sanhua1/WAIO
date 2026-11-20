using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.UI;

/// <summary>
/// 列表里的一行:分组标题,或者一个结果。列表整体重建,所以文字类属性只读;
/// 图标是异步加载完才填上的,选中 / 悬停会变,这几个需要变更通知。
/// </summary>
internal sealed class LauncherRow : INotifyPropertyChanged
{
    public const double HeaderHeight = 30;

    /// <summary>详细样式:两行(名称 + 路径),上下各留 1 的间隔。</summary>
    public const double ItemHeight = 50;

    /// <summary>极简样式(文件、文件夹默认,同 Everything):一行。</summary>
    public const double CompactHeight = 26;

    private ImageSource? _icon;
    private bool _selected;
    private bool _hovered;

    private LauncherRow(string title, LauncherItem? item, int itemIndex, string slotText, bool compact = false)
    {
        Title = title;
        Item = item;
        IsCompact = compact && item is not null;
        ItemIndex = itemIndex;
        SlotText = slotText;
        Subtitle = item?.Subtitle ?? "";
        KindLabel = item?.KindLabel ?? "";
        Glyph = GlyphFor(item?.Kind);
    }

    public static LauncherRow Header(string title) => new(title, null, -1, "");

    /// <param name="itemIndex">在 <see cref="LauncherState.Items"/> 里的位置;前 9 项显示 Ctrl+数字。</param>
    /// <param name="compact">用极简的一行样式(设置 <c>fileResultStyle</c>,只用于文件和文件夹)。</param>
    public static LauncherRow ForItem(LauncherItem item, int itemIndex, bool compact = false) =>
        new(item.Title, item, itemIndex, itemIndex < 9 ? $"Ctrl+{itemIndex + 1}" : "", compact);

    public event PropertyChangedEventHandler? PropertyChanged;

    public LauncherItem? Item { get; }

    public int ItemIndex { get; }

    public bool IsHeader => Item is null;

    public bool IsCompact { get; }

    public double Height => IsHeader ? HeaderHeight : IsCompact ? CompactHeight : ItemHeight;

    /// <summary>行与行之间的间隔(选中底色不贴在一起):详细样式上下各 1,极简样式贴紧(同 Everything)。</summary>
    public Thickness RowPadding => IsCompact || IsHeader ? new Thickness(0) : new Thickness(0, 1, 0, 1);

    public CornerRadius Corner => new(IsCompact ? 4 : 6);

    /// <summary>选中指示条的高度。</summary>
    public double BarHeight => IsCompact ? 12 : 18;

    public Visibility DetailedVisibility => IsCompact ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CompactVisibility => IsCompact ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ItemVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;

    public string Title { get; }

    public string Subtitle { get; }

    public Visibility SubtitleVisibility => Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string KindLabel { get; }

    public string SlotText { get; }

    public string Glyph { get; }

    /// <summary>没有系统图标时的字形(Segoe Fluent Icons):查看全部 / 地球 / 文档 / 文件夹 / 所有应用。</summary>
    public static string GlyphFor(LauncherItemKind? kind) => kind switch
    {
        LauncherItemKind.MoreFiles => "\uE8A9",
        LauncherItemKind.Web => "\uE774",
        LauncherItemKind.File => "\uE8A5",
        LauncherItemKind.Folder => "\uE8B7",
        _ => "\uE71D",
    };

    public bool HasPath => Item?.Path is not null;

    /// <summary>右键菜单的"复制路径":"查看全部"没有可复制的。</summary>
    public bool CanCopy => Item is { Kind: not LauncherItemKind.MoreFiles };

    /// <summary>应用图标;加载前(或网页搜索)为 null,此时显示字形图标。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (!ReferenceEquals(_icon, value))
            {
                _icon = value;
                Changed(nameof(Icon));
                Changed(nameof(GlyphVisibility));
            }
        }
    }

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                Changed(nameof(SelectedVisibility));
                Changed(nameof(HoverVisibility));
            }
        }
    }

    public bool IsHovered
    {
        get => _hovered;
        set
        {
            if (_hovered != value)
            {
                _hovered = value;
                Changed(nameof(HoverVisibility));
            }
        }
    }

    public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HoverVisibility => _hovered && !_selected ? Visibility.Visible : Visibility.Collapsed;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
