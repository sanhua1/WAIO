namespace Waio.Feature.Clipboard.Domain;

/// <summary>
/// 弹出列表的选择状态(纯逻辑,参考 MAIO 的 PickerState)。
/// 列表顺序:未固定的按最后使用时间从新到旧,固定的排在最后。默认选中第一条(最近复制或最近粘贴的)。
/// </summary>
internal sealed class PickerState
{
    /// <summary>快速粘贴编号(Ctrl+1…9、Ctrl+0)只给前 10 条未固定的条目。</summary>
    public const int QuickSlots = 10;

    private List<ClipItem> _items = [];

    public IReadOnlyList<ClipItem> Items => _items;

    /// <summary>条目集合每变一次(替换、删除)加一。视图据此判断只需移动选中,还是要重建整个列表。</summary>
    public int Version { get; private set; }

    public int SelectedIndex { get; private set; }

    public ClipItem? Selected => SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

    public static List<ClipItem> Arrange(IEnumerable<ClipItem> items)
    {
        var all = items.ToList();
        var recent = all.Where(i => !i.IsPinned).OrderByDescending(i => i.LastUsedAt);
        var pinned = all.Where(i => i.IsPinned).OrderBy(i => i.CreatedAt);
        return [.. recent, .. pinned];
    }

    /// <summary>替换列表(例如搜索结果变化)。<paramref name="keepSelection"/> 为 true 时尽量保持选中同一条。</summary>
    public void SetItems(IEnumerable<ClipItem> items, bool keepSelection = false)
    {
        var previous = Selected?.Id;
        _items = Arrange(items);
        Version++;
        var index = keepSelection && previous is { } id ? _items.FindIndex(i => i.Id == id) : -1;
        SelectedIndex = index >= 0 ? index : 0;
    }

    public void Select(int index)
    {
        if (_items.Count > 0)
        {
            SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
        }
    }

    /// <summary>上下移动,到头后循环。</summary>
    public void Move(int offset)
    {
        if (_items.Count > 0)
        {
            SelectedIndex = (((SelectedIndex + offset) % _items.Count) + _items.Count) % _items.Count;
        }
    }

    /// <summary>快速粘贴编号对应的条目:slot 1…9 为第 1…9 条,0 为第 10 条(只数未固定的)。</summary>
    public ClipItem? QuickItem(int slot)
    {
        var index = slot == 0 ? 9 : slot - 1;
        var recent = _items.Where(i => !i.IsPinned).ToList();
        return index >= 0 && index < recent.Count ? recent[index] : null;
    }

    /// <summary>某一行显示的快速粘贴编号(没有则 null)。</summary>
    public int? QuickSlotOf(ClipItem item)
    {
        if (item.IsPinned)
        {
            return null;
        }

        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0 || index >= QuickSlots)
        {
            return null;
        }

        return index == 9 ? 0 : index + 1;
    }

    public void Remove(long id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0)
        {
            return;
        }

        _items.RemoveAt(index);
        Version++;
        if (SelectedIndex >= _items.Count)
        {
            SelectedIndex = Math.Max(0, _items.Count - 1);
        }
    }
}

