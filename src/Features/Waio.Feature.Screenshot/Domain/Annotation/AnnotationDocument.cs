namespace Waio.Feature.Screenshot.Domain.Annotation;

/// <summary>标注对象列表 + 选中 + 快照式撤销 / 重做(MAIO AnnotationDocument,spec 4.2:每次修改前保存整个列表,最多 100 步)。</summary>
public sealed class AnnotationDocument
{
    public const int UndoLimit = 100;

    private readonly List<IReadOnlyList<Annotation>> _undo = [];
    private readonly List<IReadOnlyList<Annotation>> _redo = [];
    private List<Annotation> _items = [];

    /// <summary>上一次快照的合并键:连续的同类修改(拖滑块、滚轮调大小)只保存一次快照。</summary>
    private string? _lastCoalescingKey;

    public IReadOnlyList<Annotation> Items => _items;

    public Guid? SelectedId { get; set; }

    /// <summary>用滚轮调整的"下一个序号"偏移,放下一个序号后清零。</summary>
    public int CounterAdjustment { get; set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsEmpty => _items.Count == 0;

    public Annotation? Selected => SelectedId is { } id ? _items.FirstOrDefault(a => a.Id == id) : null;

    public int HighestCounterNumber => _items.Select(a => a.Shape is CounterShape c ? c.Number : 0).DefaultIfEmpty(0).Max();

    /// <summary>下一个序号:已有的最大序号 + 1,再加上滚轮调整。</summary>
    public int NextCounterNumber => Math.Max(1, HighestCounterNumber + 1 + CounterAdjustment);

    /// <summary>修改前调用,保存一份快照。<paramref name="coalescing"/> 相同的连续调用只保存第一次(合并成一步撤销)。</summary>
    public void Checkpoint(string? coalescing = null)
    {
        var same = coalescing is not null && coalescing == _lastCoalescingKey;
        _lastCoalescingKey = coalescing;
        if (same)
        {
            return;
        }

        _undo.Add(_items.ToArray());
        if (_undo.Count > UndoLimit)
        {
            _undo.RemoveRange(0, _undo.Count - UndoLimit);
        }

        _redo.Clear();
    }

    /// <summary>载入已有的标注(重新编辑时),不进撤销记录。</summary>
    public void Load(IEnumerable<Annotation> annotations)
    {
        _items = [.. annotations];
        _undo.Clear();
        _redo.Clear();
        SelectedId = null;
    }

    public void Add(Annotation annotation)
    {
        Checkpoint();
        _items.Add(annotation);
    }

    /// <summary>替换同 id 的对象,不保存快照(拖动时由调用方在开始时保存一次)。</summary>
    public void Replace(Annotation annotation)
    {
        var index = _items.FindIndex(a => a.Id == annotation.Id);
        if (index >= 0)
        {
            _items[index] = annotation;
        }
    }

    public void Remove(Guid id)
    {
        if (!_items.Any(a => a.Id == id))
        {
            return;
        }

        Checkpoint();
        _items.RemoveAll(a => a.Id == id);
        if (SelectedId == id)
        {
            SelectedId = null;
        }
    }

    public void Undo()
    {
        _lastCoalescingKey = null;
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(_items.ToArray());
        _items = [.. _undo[^1]];
        _undo.RemoveAt(_undo.Count - 1);
        DropMissingSelection();
    }

    public void Redo()
    {
        _lastCoalescingKey = null;
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(_items.ToArray());
        _items = [.. _redo[^1]];
        _redo.RemoveAt(_redo.Count - 1);
        DropMissingSelection();
    }

    /// <summary>最上层的、包含这个点的对象。</summary>
    public Annotation? Hit(PointD point, double tolerance) => _items.LastOrDefault(a => a.Contains(point, tolerance));

    private void DropMissingSelection()
    {
        if (SelectedId is { } id && !_items.Any(a => a.Id == id))
        {
            SelectedId = null;
        }
    }
}

/// <summary>
/// 把滚轮事件换算成"加一 / 减一"(MAIO WheelStepper,调整序号、大小)。
/// 滚一格不一定只来一个事件(平滑滚动、触控板会变成一串小事件)。规则:停顿超过 <see cref="BurstGap"/> 后的第一个事件立即走一步;
/// 同一串事件里继续累积,超过阈值才再走一步;换方向视为新的一串。Windows 的滚轮量一格是 120。
/// </summary>
public sealed class WheelStepper(double threshold = 120)
{
    public const double BurstGap = 0.25;

    private double? _lastTime;
    private double _accumulated;
    private int _direction;

    /// <summary>返回 +1、-1 或 0。<paramref name="delta"/> 为正表示向上滚(增大);<paramref name="time"/> 单位秒。</summary>
    public int Feed(double delta, double time)
    {
        if (delta == 0)
        {
            return 0;
        }

        var sign = delta > 0 ? 1 : -1;
        var isNewBurst = _lastTime is not { } last || time - last > BurstGap || sign != _direction;
        _lastTime = time;
        if (isNewBurst)
        {
            _direction = sign;
            _accumulated = 0;
            return sign;
        }

        _accumulated += Math.Abs(delta);
        if (_accumulated < threshold)
        {
            return 0;
        }

        _accumulated -= threshold;
        return sign;
    }
}
