using System.Threading.Channels;
using Waio.Kit.Input;

namespace Waio.Testing;

/// <summary>
/// 内存版输入服务。用 <see cref="Feed"/> 模拟一次按下事件:先交给键盘会话,否则依次过过滤器。
/// 记录合成按键、活跃的过滤器和会话,便于断言"功能停用后没有残留"。
/// </summary>
public sealed class FakeInputService : IInputService
{
    private readonly List<IRealtimeKeyFilter> _filters = [];
    private readonly List<FakeSession> _sessions = [];

    public IReadOnlyList<IRealtimeKeyFilter> ActiveFilters => _filters;

    public int ActiveSessionCount => _sessions.Count;

    public List<KeyChord> SentKeys { get; } = [];

    /// <summary>过滤器通过 <see cref="IKeyEmitter"/> 发出的单键事件(键名, 是否按下),按发出顺序。</summary>
    public List<(string Key, bool Down)> Emitted { get; } = [];

    public IDisposable RegisterKeyFilter(IRealtimeKeyFilter filter)
    {
        _filters.Add(filter);
        return new Token(() => _filters.Remove(filter));
    }

    public IKeyboardSession BeginKeyboardSession(KeyboardSessionOptions options)
    {
        var session = new FakeSession(options, s => _sessions.Remove(s));
        _sessions.Add(session);
        return session;
    }

    /// <summary>发合成按键时调用(模拟目标程序的反应,如 Ctrl+C 后写剪贴板)。</summary>
    public Action<KeyChord>? OnSendKeys { get; set; }

    public void SendKeys(KeyChord chord)
    {
        SentKeys.Add(chord);
        OnSendKeys?.Invoke(chord);
    }

    /// <summary>假的键名表:够测试用的近似(真实键名表在 Core 的 KeyNames)。可往里加键名。</summary>
    public HashSet<string> KnownKeys { get; } =
    [
        .. "abcdefghijklmnopqrstuvwxyz0123456789".Select(c => c.ToString()),
        .. Enumerable.Range(1, 24).Select(i => "f" + i),
        "esc", "capslock", "tab", "space", "enter", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
        "left", "right", "up", "down", "lctrl", "rctrl", "lshift", "rshift", "lalt", "ralt", "lwin", "rwin", "apps",
    ];

    private static readonly Dictionary<string, string> KeyAliases = new()
    {
        ["escape"] = "esc", ["caps"] = "capslock", ["return"] = "enter", ["del"] = "delete", ["back"] = "backspace",
    };

    public string? NormalizeKey(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        lower = KeyAliases.GetValueOrDefault(lower, lower);
        return KnownKeys.Contains(lower) ? lower : null;
    }

    /// <summary>调用过的 <see cref="SetLockState"/>(键名, 状态),按调用顺序。</summary>
    public List<(string Key, bool On)> LockStateRequests { get; } = [];

    public void SetLockState(string key, bool on)
    {
        if (key is not ("capslock" or "numlock" or "scrolllock"))
        {
            throw new ArgumentException($"'{key}' 不是锁定键", nameof(key));
        }

        lock (LockStateRequests)
        {
            LockStateRequests.Add((key, on));
        }
    }

    /// <summary>模拟输入引擎的"清空按住状态"(锁屏解锁后):依次调用过滤器的 OnReset。</summary>
    public void Reset()
    {
        foreach (var filter in _filters.ToArray())
        {
            filter.OnReset(_emitter);
        }
    }

    /// <summary>模拟一次按键事件(按下/抬起),返回最终处置(被会话或某个过滤器吞掉则为 Consume)。</summary>
    public FilterDecision Feed(KeyEvent keyEvent)
    {
        var session = _sessions.LastOrDefault();
        if (session is not null)
        {
            session.Writer.TryWrite(keyEvent);
            return session.Options.ConsumeEvents ? FilterDecision.Consume : FilterDecision.Pass;
        }

        foreach (var filter in _filters.ToArray())
        {
            if (filter.OnKey(keyEvent, _emitter) == FilterDecision.Consume)
            {
                return FilterDecision.Consume;
            }
        }

        return FilterDecision.Pass;
    }

    private readonly RecordingEmitter _emitter;

    public FakeInputService()
    {
        _emitter = new RecordingEmitter(Emitted);
    }

    private sealed class RecordingEmitter(List<(string Key, bool Down)> log) : IKeyEmitter
    {
        public void KeyDown(string key) => log.Add((key, true));

        public void KeyUp(string key) => log.Add((key, false));
    }

    private sealed class Token(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class FakeSession : IKeyboardSession
    {
        private readonly Channel<KeyEvent> _channel = Channel.CreateUnbounded<KeyEvent>();
        private readonly Action<FakeSession> _onDispose;

        public FakeSession(KeyboardSessionOptions options, Action<FakeSession> onDispose)
        {
            Options = options;
            _onDispose = onDispose;
        }

        public KeyboardSessionOptions Options { get; }

        public ChannelWriter<KeyEvent> Writer => _channel.Writer;

        public ChannelReader<KeyEvent> Events => _channel.Reader;

        public ValueTask DisposeAsync()
        {
            _onDispose(this);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
