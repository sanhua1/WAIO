using Waio.Feature.KeyMapping.Domain;
using Waio.Kit.Input;

namespace Waio.Feature.KeyMapping.Realtime;

/// <summary>
/// 改键、双功能键、Super Caps 层的实时过滤器(ADR-0008、ADR-0013)。在钩子线程同步运行:不阻塞、不做 IO、不起任务、不使用计时器。
/// <para>
/// 双功能键用"宽松按住"策略判定,不需要计时器:按下时先吞掉并记为 Pending;
/// 此后只要按下了<b>别的键</b>就判定为按住(先发出 hold 的按下,再放行那个键);
/// 没等到别的键就松开则判定为轻点(发出 tap 的按下+抬起)。
/// </para>
/// <para>
/// 层(Super Caps):层键(CapsLock)的按下、重复、抬起<b>全部吞掉</b>——系统收不到,大写锁定不会切换。
/// 按住层键期间<b>首次按下</b>的映射键改发目标键(W → ↑),它的重复和抬起也一直按目标键处理,直到它自己抬起为止
/// (先松开 CapsLock 再松开 W,发出的也是 ↑ 的抬起,不会留下卡住的键)。没映射的键照常放行;Shift/Ctrl 等物理修饰键不受影响,
/// 所以 CapsLock+Shift+D = Shift+→(选中)。
/// </para>
/// 规则集从其他线程发布(<see cref="Publish"/>),钩子线程在下一个事件上切换,并释放旧规则里还卡着的按住状态。
/// </summary>
internal sealed class RemapFilter : IRealtimeKeyFilter
{
    private RuleSet _incoming = RuleSet.Empty; // 其他线程写
    private RuleSet _current = RuleSet.Empty; // 以下只有钩子线程读写

    /// <summary>层键是否按住。规则切换时保留(用户可能正按着 CapsLock 改设置)。</summary>
    private bool _layerHeld;

    /// <summary>正被层映射着的键:源键 → 已发出按下的目标键。</summary>
    private readonly Dictionary<string, string> _layerActive = new(StringComparer.Ordinal);

    public void Publish(RuleSet rules) => Volatile.Write(ref _incoming, rules);

    public FilterDecision OnKey(in KeyEvent keyEvent, IKeyEmitter emitter)
    {
        var incoming = Volatile.Read(ref _incoming);
        if (!ReferenceEquals(incoming, _current))
        {
            ReleaseHeld(_current, emitter); // 切换规则前,别让旧规则的 hold 键、层的目标键一直按着
            ReleaseLayer(emitter);
            _layerHeld &= incoming.Layer is not null; // 层被关掉:之后再打开时不能沿用旧的"按住中"
            _current = incoming;
        }

        var rules = _current;
        if (rules.IsEmpty)
        {
            return FilterDecision.Pass;
        }

        var isDown = keyEvent.Kind == KeyEventKind.Down;
        if (rules.Layer is { } layer && OnLayer(layer, rules, keyEvent, isDown, emitter))
        {
            return FilterDecision.Consume;
        }

        if (isDown)
        {
            PromotePendingExcept(rules, keyEvent.Key, emitter);
        }

        if (!rules.TryGet(keyEvent.Key, out var rule))
        {
            return FilterDecision.Pass;
        }

        return rule.IsTapHold ? HandleTapHold(rule, keyEvent, emitter) : HandleRemap(rule, isDown, emitter);
    }

    /// <summary>锁屏解锁后:期间的抬起都收不到,所有按住状态作废,已发出的按下补发抬起。</summary>
    public void OnReset(IKeyEmitter emitter)
    {
        ReleaseHeld(_current, emitter);
        ReleaseLayer(emitter);
        _layerHeld = false;
    }

    /// <returns>true = 层处理了这个事件(吞掉);false = 交给普通规则。</returns>
    private bool OnLayer(Layer layer, RuleSet rules, in KeyEvent keyEvent, bool isDown, IKeyEmitter emitter)
    {
        var key = keyEvent.Key;
        if (key == layer.Trigger)
        {
            if (isDown && !keyEvent.IsRepeat)
            {
                PromotePendingExcept(rules, key, emitter); // 和按下其他键一样:正在等待的双功能键判定为按住
            }

            _layerHeld = isDown;
            return true;
        }

        if (_layerActive.TryGetValue(key, out var active))
        {
            if (isDown && keyEvent.IsRepeat)
            {
                emitter.KeyDown(active); // 长按 W = 目标程序看到 ↑ 在自动重复
                return true;
            }

            // 抬起;或者"非重复的按下"(它的抬起丢了):先收尾,再按当前状态重新判断
            emitter.KeyUp(active);
            _layerActive.Remove(key);
            if (!isDown)
            {
                return true;
            }
        }

        if (!isDown || keyEvent.IsRepeat || !_layerHeld || !layer.Map.TryGetValue(key, out var target))
        {
            return false; // 层键按下之前就按着的键,它的重复和抬起照常处理
        }

        PromotePendingExcept(rules, key, emitter);
        emitter.KeyDown(target);
        _layerActive[key] = target;
        return true;
    }

    private void ReleaseLayer(IKeyEmitter emitter)
    {
        foreach (var target in _layerActive.Values)
        {
            emitter.KeyUp(target);
        }

        _layerActive.Clear();
    }

    private static FilterDecision HandleRemap(Rule rule, bool isDown, IKeyEmitter emitter)
    {
        if (isDown)
        {
            emitter.KeyDown(rule.To!); // 长按重复也照发,目标程序看到的就是 to 键在自动重复
        }
        else
        {
            emitter.KeyUp(rule.To!);
        }

        return FilterDecision.Consume;
    }

    private static FilterDecision HandleTapHold(Rule rule, in KeyEvent keyEvent, IKeyEmitter emitter)
    {
        if (keyEvent.Kind == KeyEventKind.Down)
        {
            if (!keyEvent.IsRepeat)
            {
                if (rule.State == HoldState.Holding)
                {
                    emitter.KeyUp(rule.Hold!); // 上一次的"抬起"事件丢了(如锁屏):先收尾,再当作新的一次按下
                }

                rule.State = HoldState.Pending;
            }

            return FilterDecision.Consume;
        }

        switch (rule.State)
        {
            case HoldState.Pending:
                emitter.KeyDown(rule.Tap!);
                emitter.KeyUp(rule.Tap!);
                break;
            case HoldState.Holding:
                emitter.KeyUp(rule.Hold!);
                break;
        }

        rule.State = HoldState.Idle;
        return FilterDecision.Consume;
    }

    /// <summary>别的键被按下:所有处于 Pending 的双功能键判定为"按住",先发出它们的 hold 按下。</summary>
    private static void PromotePendingExcept(RuleSet rules, string pressedKey, IKeyEmitter emitter)
    {
        foreach (var rule in rules.TapHoldRules)
        {
            if (rule.State == HoldState.Pending && rule.From != pressedKey)
            {
                emitter.KeyDown(rule.Hold!);
                rule.State = HoldState.Holding;
            }
        }
    }

    private static void ReleaseHeld(RuleSet rules, IKeyEmitter emitter)
    {
        foreach (var rule in rules.TapHoldRules)
        {
            if (rule.State == HoldState.Holding)
            {
                emitter.KeyUp(rule.Hold!);
            }

            rule.State = HoldState.Idle;
        }
    }
}
