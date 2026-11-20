using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Clipboard.Domain;

namespace Waio.Feature.Clipboard.Views;

/// <summary>
/// 列表里的一行(视图模型)。列表整体重建,所以文字类属性只读;
/// 只有缩略图是异步加载完才填上的,需要变更通知。
/// </summary>
internal sealed class ClipRow : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public ClipRow(ClipItem item, int? quickSlot, DateTimeOffset now)
    {
        Item = item;
        SlotText = quickSlot?.ToString() ?? "";
        SlotVisibility = quickSlot is null ? Visibility.Collapsed : Visibility.Visible;
        KindGlyph = item.Kind switch
        {
            ClipKind.Image => "\uE91B",
            ClipKind.Files => "\uE8A5",
            ClipKind.RichText => "\uE8D2",
            _ => "\uE8E4", // 几行文字
        };
        Summary = item.Summary;
        var time = RelativeTime.Format(item.LastUsedAt, now);
        Meta = string.IsNullOrEmpty(item.SourceApp) ? time : $"{item.SourceApp} · {time}";
        PinVisibility = item.IsPinned ? Visibility.Visible : Visibility.Collapsed;
        PinText = item.IsPinned ? "取消固定" : "固定";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClipItem Item { get; }

    public bool HasImage => Item.HasImage;

    /// <summary>Ctrl+数字的快速粘贴编号;没有则为空。</summary>
    public string SlotText { get; }

    public Visibility SlotVisibility { get; }

    public string KindGlyph { get; }

    public string Summary { get; }

    /// <summary>"来源程序 · 几分钟前"。</summary>
    public string Meta { get; }

    public Visibility PinVisibility { get; }

    public string PinText { get; }

    /// <summary>图片缩略图;加载前(或不是图片)为 null,此时显示类型图标。</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconVisibility)));
            }
        }
    }

    public Visibility IconVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>键盘选中(高亮底色)。</summary>
    public bool IsSelected
    {
        get => _selected;
        set => SetFlag(ref _selected, value, nameof(SelectedVisibility));
    }

    /// <summary>鼠标悬停(更淡的底色)。</summary>
    public bool IsHovered
    {
        get => _hovered;
        set => SetFlag(ref _hovered, value, nameof(HoverVisibility));
    }

    public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HoverVisibility => _hovered && !_selected ? Visibility.Visible : Visibility.Collapsed;

    private bool _selected;
    private bool _hovered;

    private void SetFlag(ref bool field, bool value, string changed)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(changed));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HoverVisibility)));
        }
    }
}
