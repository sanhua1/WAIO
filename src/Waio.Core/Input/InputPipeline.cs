using System.Threading.Channels;
using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>
/// 键盘事件管线(ARCHITECTURE §5.1):规范化 → 键盘会话 → 实时过滤器 → 快捷键匹配 → 异步分发。
/// 纯逻辑,不碰操作系统,因此可以完整单测。<see cref="Process"/> 只会在钩子线程上被调用,
/// 内部的按键状态因此无需加锁;过滤器/会话/匹配器的注册来自其他线程,用写时复制 + volatile 交换。
/// <para>
/// ⚠️ <see cref="Process"/> 在钩子回调里同步执行:必须极快,不得阻塞、不得 await(超时系统会静默摘除钩子)。
/// </para>
/// </summary>
internal sealed partial class InputPipeline
{
    // 修饰键位 → 虚拟键码,用于与物理状态核对
    private static readonly (int Bit, int Vk)[] ModifierKeys =
    [
        (KeyNames.LCtrl, 0xA2), (KeyNames.RCtrl, 0xA3), (KeyNames.LShift, 0xA0), (KeyNames.RShift, 0xA1),
        (KeyNames.LAlt, 0xA4), (KeyNames.RAlt, 0xA5), (KeyNames.LWin, 0x5B), (KeyNames.RWin, 0x5C),
    ];

    private readonly IKeySender _sender;
    private readonly SenderKeyEmitter _emitter;
    private readonly IKeyStateProbe _probe;
    private readonly Action<CommandBinding> _dispatch;
    private readonly Func<string?> _foregroundProcess;
    private readonly Action<Exception> _onError;
    private long _resyncs;

    private readonly object _gate = new();
    private IRealtimeKeyFilter[] _filters = [];
    private SessionState[] _sessions = [];
    private BindingMatcher _matcher = BindingMatcher.Empty;

    // 以下状态只在钩子线程访问
    private readonly HashSet<int> _down = [];
    private readonly HashSet<int> _consumed = [];
    private readonly Dictionary<int, uint> _lastDown = [];
    private int _modifierMask;
    private bool _resetRequested;

    /// <summary>超过这个间隔的"重复"按下视为新的按下(Windows 键盘重复延迟最大 1 秒,留一点余量)。</summary>
    private const uint MaxRepeatGapMs = 1100;

    public InputPipeline(
        IKeySender sender,
        IKeyStateProbe probe,
        Action<CommandBinding> dispatch,
        Func<string?> foregroundProcess,
        Action<Exception> onError)
    {
        _sender = sender;
        _emitter = new SenderKeyEmitter(sender);
        _probe = probe;
        _dispatch = dispatch;
        _foregroundProcess = foregroundProcess;
        _onError = onError;
    }

    /// <summary>
    /// 用户此刻按着的标准修饰键的虚拟键码(左右区分)。在钩子线程之外读取是近似值(可能差一个事件),
    /// 发合成按键前"先松开用户按着的修饰键"用它足够。
    /// </summary>
    public IReadOnlyList<int> HeldModifierVks()
    {
        var mask = Volatile.Read(ref _modifierMask);
        return [.. ModifierKeys.Where(m => (mask & m.Bit) != 0).Select(m => m.Vk)];
    }

    /// <summary>因事件丢失而与物理状态重新同步的次数(诊断用;正常使用应接近 0)。</summary>
    public long Resyncs => Volatile.Read(ref _resyncs);

    public void SetMatcher(BindingMatcher matcher) => Volatile.Write(ref _matcher, matcher);

    public IDisposable AddFilter(IRealtimeKeyFilter filter)
    {
        lock (_gate)
        {
            _filters = [.. _filters, filter];
        }

        return new Removal(() =>
        {
            lock (_gate)
            {
                _filters = [.. _filters.Where(f => !ReferenceEquals(f, filter))];
            }
        });
    }

    public IKeyboardSession BeginSession(KeyboardSessionOptions options)
    {
        var session = new SessionState(options, EndSession);
        lock (_gate)
        {
            _sessions = [.. _sessions, session];
        }

        return session;
    }

    /// <summary>
    /// 请求清空"按住中"的记录(锁屏解锁、切换会话后调用:期间的抬起事件收不到)。可在任意线程调用,下一个按键事件时生效。
    /// </summary>
    public void RequestReset() => Volatile.Write(ref _resetRequested, true);

    /// <returns>true = 吞掉这个事件,目标程序收不到。</returns>
    public bool Process(in RawKey raw)
    {
        if (raw.IsOurs || !KeyNames.TryGetName(raw.VkCode, out var key))
        {
            return false; // 自己发的事件、或不认识的键:原样放行
        }

        if (Volatile.Read(ref _resetRequested))
        {
            Volatile.Write(ref _resetRequested, false);
            ResetHeldState();
        }

        var bit = KeyNames.ModifierBit(raw.VkCode);
        var isRepeat = false;
        var upOfConsumed = false;
        if (raw.IsDown)
        {
            isRepeat = !_down.Add(raw.VkCode);
            if (isRepeat && LostUp(raw))
            {
                // 我们以为它一直按着,实际早就抬起了——"抬起"事件丢了。这不是长按重复,而是一次新的按下
                isRepeat = false;
                _consumed.Remove(raw.VkCode);
                CountResync();
            }

            _lastDown[raw.VkCode] = raw.Time;

            if (bit == 0)
            {
                ResyncModifiers(); // 非修饰键按下时,核对修饰键是否有"卡住"的
            }

            _modifierMask |= bit;
        }
        else
        {
            _down.Remove(raw.VkCode);
            _modifierMask &= ~bit;
            upOfConsumed = _consumed.Remove(raw.VkCode); // 按下时被吞,抬起也要吞,否则目标程序收到孤儿抬起
        }

        var modifiers = KeyNames.ToModifiers(_modifierMask);
        var keyEvent = new KeyEvent(raw.IsDown ? KeyEventKind.Down : KeyEventKind.Up, key, modifiers, isRepeat);
        var isModifier = bit != 0;
        var consume = false;

        var sessions = Volatile.Read(ref _sessions);
        if (sessions.Length > 0)
        {
            var session = sessions[^1];
            session.Writer.TryWrite(keyEvent);

            // 会话里绝不吞修饰键事件:吞了 Alt 的抬起,目标程序会以为 Alt 一直按着
            consume = raw.IsDown && !isModifier && session.Options.ConsumeEvents;

            // 吞掉 Win/Alt 组合里的主键后,系统只看到"Win/Alt 单按又松开",会弹出开始菜单/激活菜单栏;
            // 和快捷键命中时一样,插一个无意义的按键把它抵消掉(录制 Win+V 时尤其明显)
            if (consume && !isRepeat && (modifiers & (KeyModifiers.Win | KeyModifiers.Alt)) != 0)
            {
                _sender.TapMaskKey();
            }

            if (!raw.IsDown && isModifier && (session.Options.EndOnRelease & KeyNames.ToModifiers(bit)) != 0)
            {
                EndSession(session);
            }
        }
        else
        {
            // 过滤器看到所有事件(按下/抬起/重复),改键靠它;快捷键匹配只看首次按下
            var filtered = RunFilters(keyEvent);
            consume = filtered
                || (IsHoldKey(key)
                    ? OnHoldKey(raw, key, modifiers, isRepeat)
                    : raw.IsDown && (isRepeat && _consumed.Contains(raw.VkCode) || Match(keyEvent, isModifier, isRepeat)));
        }

        if (raw.IsDown && consume)
        {
            _consumed.Add(raw.VkCode);
        }

        return consume || upOfConsumed;
    }

    private bool RunFilters(in KeyEvent keyEvent)
    {
        foreach (var filter in Volatile.Read(ref _filters))
        {
            try
            {
                if (filter.OnKey(keyEvent, _emitter) == FilterDecision.Consume)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _onError(ex); // 一个有 bug 的过滤器不能拖垮整条管线
            }
        }

        return false;
    }

    private bool Match(in KeyEvent keyEvent, bool isModifier, bool isRepeat)
    {
        if (isModifier || isRepeat)
        {
            return false;
        }

        if (_held is not null)
        {
            _heldUsed = true; // 按住自定义修饰键期间按了别的键:松开时不再补发它本身
        }

        return Fire(new KeyChord(keyEvent.Modifiers, keyEvent.Key, _held));
    }

    /// <summary>按组合查绑定;命中就分发并返回 true(调用方吞掉按键)。</summary>
    private bool Fire(KeyChord chord)
    {
        var binding = Volatile.Read(ref _matcher).Match(chord, _foregroundProcess());
        if (binding is null)
        {
            return false;
        }

        if ((chord.Modifiers & (KeyModifiers.Win | KeyModifiers.Alt)) != 0)
        {
            _sender.TapMaskKey();
        }

        _dispatch(binding); // 只入队,不在钩子线程执行
        return true;
    }

    /// <summary>清除物理上已松开、但我们仍记为按下的修饰键(典型场景:Win+L 锁屏后收不到 Win 的抬起)。</summary>
    private void ResyncModifiers()
    {
        if (_modifierMask == 0)
        {
            return;
        }

        foreach (var (bit, vk) in ModifierKeys)
        {
            if ((_modifierMask & bit) != 0 && !_probe.IsDown(vk))
            {
                _modifierMask &= ~bit;
                _down.Remove(vk);
                CountResync();
            }
        }
    }

    private void CountResync() => Volatile.Write(ref _resyncs, _resyncs + 1);

    /// <summary>
    /// 这次"重复"按下其实是丢了抬起之后的新按下吗?
    /// 放行的键:问系统真实状态。被我们吞掉的键:系统状态不会更新(见 <see cref="IKeyStateProbe"/>),
    /// 只能看时间——自动重复的间隔最长约 1 秒(系统设置的最大延迟),隔得更久就不是重复。
    /// </summary>
    private bool LostUp(in RawKey raw)
    {
        if (!_consumed.Contains(raw.VkCode))
        {
            return !_probe.IsDown(raw.VkCode);
        }

        return raw.Time != 0 && _lastDown.TryGetValue(raw.VkCode, out var last) && last != 0
            && unchecked(raw.Time - last) > MaxRepeatGapMs;
    }

    /// <summary>把被吞掉的键、自定义修饰键的按住记录全部清掉(它们的状态无法向系统核对)。</summary>
    private void ResetHeldState()
    {
        foreach (var vk in _consumed)
        {
            _down.Remove(vk);
        }

        _consumed.Clear();
        _held = null;

        foreach (var filter in Volatile.Read(ref _filters))
        {
            try
            {
                filter.OnReset(_emitter); // 过滤器自己的按住状态(双功能键、Super Caps 层)也作废
            }
            catch (Exception ex)
            {
                _onError(ex);
            }
        }
    }

    private void EndSession(SessionState session)
    {
        lock (_gate)
        {
            _sessions = [.. _sessions.Where(s => !ReferenceEquals(s, session))];
        }

        session.Writer.TryComplete();
    }

    private sealed class Removal(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class SessionState : IKeyboardSession
    {
        private readonly Channel<KeyEvent> _channel = Channel.CreateUnbounded<KeyEvent>(new UnboundedChannelOptions
        {
            SingleReader = true, // 不设 SingleWriter:会话可能被另一个线程 Dispose(完成通道),与钩子线程写入并发
        });

        private readonly Action<SessionState> _end;

        public SessionState(KeyboardSessionOptions options, Action<SessionState> end)
        {
            Options = options;
            _end = end;
        }

        public KeyboardSessionOptions Options { get; }

        public ChannelWriter<KeyEvent> Writer => _channel.Writer;

        public ChannelReader<KeyEvent> Events => _channel.Reader;

        public ValueTask DisposeAsync()
        {
            _end(this);
            return ValueTask.CompletedTask;
        }
    }
}
