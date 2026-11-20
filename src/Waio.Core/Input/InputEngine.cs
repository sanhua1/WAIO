using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>
/// 输入引擎:全仓库唯一创建系统键盘钩子的地方(ARCHITECTURE §5)。
/// 钩子线程 → <see cref="InputPipeline"/> → 命中快捷键时入队 → 后台分发到命令总线(不占用钩子线程)。
/// 功能通过 <see cref="IInputService"/> 访问它(经宿主包装,停用时自动释放)。
/// </summary>
public sealed class InputEngine : IInputService, IAsyncDisposable
{
    private readonly ICommandBus _bus;
    private readonly ILogger _logger;
    private readonly KeySender _sender = new();
    private readonly InputPipeline _pipeline;
    private readonly KeyboardHook _hook;
    private readonly Channel<CommandBinding> _queue = Channel.CreateUnbounded<CommandBinding>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<int, bool> _pendingLocks = [];
    private Task? _dispatcher;
    private bool _started;

    /// <param name="foregroundProcess">返回前台进程名(不含 .exe),用于按进程的作用域匹配;未知返回 null。</param>
    public InputEngine(ICommandBus bus, ILogger logger, Func<string?> foregroundProcess)
    {
        _bus = bus;
        _logger = logger;
        _pipeline = new InputPipeline(
            _sender,
            new WindowsKeyStateProbe(),
            binding => _queue.Writer.TryWrite(binding),
            foregroundProcess,
            ex => logger.LogError(ex, "实时过滤器抛出异常(已忽略)"));
        _hook = new KeyboardHook(_pipeline, logger);
    }

    public InputDiagnostics Diagnostics => _hook.Diagnostics;

    /// <summary>
    /// 把 WAIO 的钩子重新排到钩子链最前(别的程序后装的钩子会先拿到按键,见 <see cref="KeyboardHook.Reassert"/>)。
    /// 钩子线程上异步执行,任意线程可调用;钩子也会每 2 秒自己做一次。未启动时什么都不做。
    /// </summary>
    public void ReassertHookPriority()
    {
        if (_started)
        {
            _hook.Reassert();
        }
    }

    /// <summary>钩子已安装、快捷键在生效(<c>--no-hook</c> 启动时为 false)。</summary>
    public bool IsRunning => _started;

    /// <summary>
    /// 录制一个快捷键(ADR-0011):开一个吞掉按键的键盘会话——录制期间快捷键匹配暂停、按键不会传给其他程序,
    /// 所以能录到 Win+V 这类会被系统或 WAIO 自己抢走的组合。规则见 <see cref="ChordRecorder"/>。
    /// </summary>
    /// <param name="onHint">录制中的提示(在线程池线程上调用,UI 需要自己切回 UI 线程)。</param>
    /// <param name="cancellationToken">取消 = 结果 <see cref="ChordRecordOutcome.Cancelled"/>。</param>
    public async Task<ChordRecordResult> RecordChordAsync(Action<string>? onHint, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return new ChordRecordResult(ChordRecordOutcome.Unavailable);
        }

        var recorder = new ChordRecorder(_pipeline.CustomModifiers);
        var session = _pipeline.BeginSession(new KeyboardSessionOptions(ConsumeEvents: true));
        await using (session.ConfigureAwait(false))
        {
            try
            {
                await foreach (var keyEvent in session.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var step = recorder.Feed(keyEvent);
                    if (step.Result is { } result)
                    {
                        return result;
                    }

                    if (step.Hint is { } hint)
                    {
                        onHint?.Invoke(hint);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 用户点了别处 / 超时:按取消处理
            }
        }

        return new ChordRecordResult(ChordRecordOutcome.Cancelled);
    }

    /// <summary>启动钩子线程与分发循环。安装钩子失败会抛异常。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _hook.Start();
        _dispatcher = Task.Run(DispatchLoopAsync);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _started = true;
        _logger.LogInformation("输入引擎已启动");

        KeyValuePair<int, bool>[] pending;
        lock (_pendingLocks)
        {
            pending = [.. _pendingLocks];
            _pendingLocks.Clear();
        }

        foreach (var (vk, on) in pending)
        {
            ApplyLockState(vk, KeyNames.TryGetName(vk, out var name) ? name : vk.ToString(), on);
        }
    }

    /// <summary>用新的绑定集合替换匹配器(原子替换,钩子线程不受影响)。</summary>
    public void SetBindings(IEnumerable<CommandBinding> bindings)
    {
        _pipeline.SetMatcher(BindingMatcher.Build(bindings));
    }

    /// <summary>设置自定义修饰键(ADR-0012),立即生效。</summary>
    public void SetCustomModifiers(IEnumerable<string> keys) => _pipeline.SetCustomModifiers(keys);

    /// <summary>远程桌面让出按键(ADR-0033):这些程序(进程名,不含 .exe)在前台时,快捷键和改键都不生效。空 = 关闭。立即生效。</summary>
    public void SetPassthroughApps(IEnumerable<string> processNames) => _pipeline.SetPassthroughApps(processNames);

    public IDisposable RegisterKeyFilter(IRealtimeKeyFilter filter) => _pipeline.AddFilter(filter);

    public IKeyboardSession BeginKeyboardSession(KeyboardSessionOptions options) => _pipeline.BeginSession(options);

    /// <summary>发出组合键;先松开用户按着、但不属于这个组合的修饰键(见 <see cref="KeySender.Send(KeyChord, IReadOnlyList{int})"/>)。</summary>
    public void SendKeys(KeyChord chord) => _sender.Send(chord, _pipeline.HeldModifierVks());

    public string? NormalizeKey(string name) => KeyNames.IsKnown(name) ? KeyNames.Canonicalize(name) : null;

    /// <summary>
    /// 读锁定状态(GetKeyState 低位),不一致就发一次该键的按下+抬起(带 WAIO 标记:管线放行,系统照常切换)。
    /// GetKeyState 是"线程视角"的状态,在没处理过键盘消息的线程上通常与系统一致——<b>未在真机验证</b>。
    /// </summary>
    public void SetLockState(string key, bool on)
    {
        var name = NormalizeKey(key);
        if (name is not ("capslock" or "numlock" or "scrolllock") || !KeyNames.TryGetVk(name, out var vk))
        {
            throw new ArgumentException($"'{key}' 不是锁定键", nameof(key));
        }

        if (!_started)
        {
            // 功能比钩子先激活:先记下,Start 时再执行(--no-hook 时永远不执行——没有钩子就没有 Super Caps,不该动用户的锁定键)
            lock (_pendingLocks)
            {
                _pendingLocks[vk] = on;
            }

            return;
        }

        ApplyLockState(vk, name, on);
    }

    private void ApplyLockState(int vk, string name, bool on)
    {
        if (((InputNative.GetKeyState(vk) & 1) != 0) == on)
        {
            return;
        }

        _sender.SendKey(vk, down: true);
        _sender.SendKey(vk, down: false);
        _logger.LogInformation("已把 {Key} 的锁定状态切换为{State}", name, on ? "开" : "关");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return;
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hook.Dispose();
        _queue.Writer.TryComplete();
        if (_dispatcher is not null)
        {
            await _dispatcher.ConfigureAwait(false);
        }

        _started = false;
    }

    /// <summary>锁屏返回/远程会话切换后系统可能已静默摘除钩子,重装一次(ARCHITECTURE §5.2 第 3 条)。</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            _pipeline.RequestReset(); // 锁屏期间的抬起事件收不到,被吞掉的键(含自定义修饰键)的按住记录作废
            _hook.Reinstall();
        }
    }

    private async Task DispatchLoopAsync()
    {
        await foreach (var binding in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // 每个命令独立执行:慢命令不会阻塞后面的快捷键;启动顺序仍然保持
            _ = RunAsync(binding);
        }
    }

    private async Task RunAsync(CommandBinding binding)
    {
        try
        {
            await _bus.PerformAsync(binding.Command, binding.Arguments).ConfigureAwait(false);
        }
        catch (CommandException ex)
        {
            _logger.LogWarning("快捷键触发命令 {Command} 失败:{Message}", binding.Command, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "快捷键触发命令 {Command} 时出现未预期的异常", binding.Command);
        }
    }
}
