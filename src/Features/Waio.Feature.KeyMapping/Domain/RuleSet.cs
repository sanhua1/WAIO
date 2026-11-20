namespace Waio.Feature.KeyMapping.Domain;

internal enum HoldState
{
    Idle,

    /// <summary>已按下,尚未决定是轻点还是按住(还没发出任何按键)。</summary>
    Pending,

    /// <summary>已判定为按住:已发出 hold 键的按下,抬起时要发出它的抬起。</summary>
    Holding,
}

/// <summary>一条已校验的规则。键名都是规范名。</summary>
internal sealed class Rule
{
    public required string From { get; init; }

    /// <summary>非空 = 简单改键。</summary>
    public string? To { get; init; }

    public string? Tap { get; init; }

    public string? Hold { get; init; }

    public bool IsTapHold => To is null;

    /// <summary>双功能键的运行状态。只在钩子线程读写(所以不需要锁);随所属 RuleSet 一起被替换。</summary>
    public HoldState State { get; set; }
}

/// <summary>
/// 一组生效的规则:构造后不可变(除各规则的钩子线程状态)。
/// 设置变化时整体替换,过滤器在下一个按键事件上切换并释放旧规则里卡住的按住状态。
/// </summary>
internal sealed class RuleSet
{
    private readonly Dictionary<string, Rule> _byKey;

    public RuleSet(IReadOnlyList<Rule> rules, Layer? layer = null)
    {
        _byKey = rules.ToDictionary(r => r.From);
        TapHoldRules = [.. rules.Where(r => r.IsTapHold)];
        Layer = layer;
    }

    public static RuleSet Empty { get; } = new([]);

    public bool IsEmpty => _byKey.Count == 0 && Layer is null;

    /// <summary>换层键(Super Caps);没启用为 null。</summary>
    public Layer? Layer { get; }

    public Rule[] TapHoldRules { get; }

    public int Count => _byKey.Count;

    public bool TryGet(string key, out Rule rule) => _byKey.TryGetValue(key, out rule!);
}

/// <summary>
/// 一个层(ADR-0013):按住 <see cref="Trigger"/> 时,<see cref="Map"/> 里的键变成目标键。层键本身的按下和抬起总是被吞掉。
/// 不可变;运行状态(层键是否按住、哪些键正被映射着)在过滤器里。
/// </summary>
internal sealed class Layer(string trigger, IReadOnlyDictionary<string, string> map)
{
    public string Trigger { get; } = trigger;

    public IReadOnlyDictionary<string, string> Map { get; } = map;
}
