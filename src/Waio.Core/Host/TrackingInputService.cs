using Waio.Kit.Input;

namespace Waio.Core.Host;

/// <summary>
/// 包装共享的输入服务,记录某个功能申请过的过滤器和键盘会话,功能停用时由宿主统一释放。
/// 功能忘了 Dispose、或在 DeactivateAsync 里抛异常,都不会让钩子里留下僵尸过滤器。
/// </summary>
internal sealed class TrackingInputService(IInputService inner) : IInputService, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _filters = [];
    private readonly List<IKeyboardSession> _sessions = [];

    public IDisposable RegisterKeyFilter(IRealtimeKeyFilter filter)
    {
        var token = inner.RegisterKeyFilter(filter);
        lock (_gate)
        {
            _filters.Add(token);
        }

        return token;
    }

    public IKeyboardSession BeginKeyboardSession(KeyboardSessionOptions options)
    {
        var session = inner.BeginKeyboardSession(options);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        return session;
    }

    public void SendKeys(KeyChord chord) => inner.SendKeys(chord);

    public string? NormalizeKey(string name) => inner.NormalizeKey(name);

    public void SetLockState(string key, bool on) => inner.SetLockState(key, on);

    public async ValueTask DisposeAsync()
    {
        IDisposable[] filters;
        IKeyboardSession[] sessions;
        lock (_gate)
        {
            filters = [.. _filters];
            sessions = [.. _sessions];
            _filters.Clear();
            _sessions.Clear();
        }

        foreach (var filter in filters)
        {
            filter.Dispose();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
