using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Waio.Core.Input;

/// <summary>钩子回调耗时统计,用来监控"离系统摘钩子的红线还有多远"。</summary>
/// <param name="StateResyncs">因按键事件丢失而与物理状态重新同步的次数。正常应接近 0;持续增长说明钩子在漏事件(如回调太慢)。</param>
/// <param name="PhysicalEvents">非注入(真实键盘)事件数。集成测试用它判断"测试期间用户碰过键盘",碰过则测量无效并中止。</param>
public sealed record InputDiagnostics(
    long EventsProcessed,
    double MaxCallbackMicroseconds,
    double TotalCallbackMicroseconds,
    long Errors,
    long StateResyncs,
    long PhysicalEvents)
{
    public double AverageCallbackMicroseconds => EventsProcessed == 0 ? 0 : TotalCallbackMicroseconds / EventsProcessed;
}

/// <summary>
/// 低级键盘钩子:专用线程 + 消息循环(ARCHITECTURE §5.1,绝不在 UI 线程)。
/// 回调只做"转交管线并返回",任何异常都被吞掉并计数——异常传回原生代码会让整个进程崩溃。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int MaxLoggedErrors = 10; // 回调里不能因为刷日志而变慢

    private readonly InputPipeline _pipeline;
    private readonly ILogger _logger;
    private readonly InputNative.HookProc _proc; // 必须持有引用,否则被 GC 回收后回调会崩溃
    private readonly ManualResetEventSlim _started = new();

    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Timer? _reassertTimer;
    private long _reasserts;

    /// <summary>定期重新排到钩子链最前的间隔。两次系统调用,开销可以忽略。</summary>
    private static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(2);
    private Exception? _startError;

    private long _events;
    private long _maxTicks;
    private long _totalTicks;
    private long _errors;
    private long _physical;

    public KeyboardHook(InputPipeline pipeline, ILogger logger)
    {
        _pipeline = pipeline;
        _logger = logger;
        _proc = OnHook;
    }

    public InputDiagnostics Diagnostics
    {
        get
        {
            var perTick = 1_000_000.0 / Stopwatch.Frequency;
            return new InputDiagnostics(
                Volatile.Read(ref _events),
                Volatile.Read(ref _maxTicks) * perTick,
                Volatile.Read(ref _totalTicks) * perTick,
                Volatile.Read(ref _errors),
                _pipeline.Resyncs,
                Volatile.Read(ref _physical));
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("钩子已经启动");
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "WAIO.KeyboardHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _started.Wait();
        if (_startError is not null)
        {
            throw new InvalidOperationException("安装键盘钩子失败", _startError);
        }

        _reassertTimer = new Timer(_ => Reassert(), null, ReassertInterval, ReassertInterval);
    }

    /// <summary>重新安装钩子(锁屏返回、会话切换后系统可能已摘除旧钩子)。异步:投递给钩子线程执行。</summary>
    public void Reinstall() => Post(quiet: false);

    /// <summary>
    /// 重新排到钩子链最前面(安静版的 <see cref="Reinstall"/>,不记日志)。
    /// <para>
    /// 多个低级键盘钩子时,<b>后安装的先拿到按键</b>。别的程序在 WAIO 之后装了钩子(实测:微信的 Alt+A 截图就是用钩子做的,
    /// Weixin.dll 引用 SetWindowsHookExW、不引用 RegisterHotKey),它会先看到 WAIO 本该吞掉的物理按键——
    /// 2026-11-10 用户报告:Alt+Tab 切窗时按 Caps+A(应为 ←),微信看到 Alt+A 弹出了截图。
    /// 所以定期、以及前台窗口变化时重新排到最前。先装新的再摘旧的,中间没有空档;两步都在钩子线程上连续执行,
    /// 期间不处理消息,不会有按键被处理两次。
    /// </para>
    /// </summary>
    public void Reassert() => Post(quiet: true);

    /// <summary>已经重新排到最前的次数(诊断用)。</summary>
    public long Reasserts => Volatile.Read(ref _reasserts);

    private void Post(bool quiet)
    {
        if (_threadId != 0)
        {
            InputNative.PostThreadMessageW(_threadId, InputNative.WM_APP_REINSTALL, quiet ? 1u : 0u, 0);
        }
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        _reassertTimer?.Dispose();
        InputNative.PostThreadMessageW(_threadId, InputNative.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Run()
    {
        _threadId = InputNative.GetCurrentThreadId();
        InputNative.PeekMessageW(out _, 0, InputNative.WM_USER, InputNative.WM_USER, InputNative.PM_NOREMOVE); // 强制创建消息队列
        try
        {
            Install();
        }
        catch (Exception ex)
        {
            _startError = ex;
        }
        finally
        {
            _started.Set();
        }

        if (_startError is not null)
        {
            return;
        }

        while (InputNative.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.Message == InputNative.WM_APP_REINSTALL)
            {
                Renew(quiet: msg.WParam != 0);
            }
        }

        Uninstall();
    }

    private void Install() => _hook = NewHook();

    private nint NewHook()
    {
        var hook = InputNative.SetWindowsHookExW(InputNative.WH_KEYBOARD_LL, _proc, InputNative.GetModuleHandleW(null), 0);
        return hook != 0 ? hook : throw new InvalidOperationException($"SetWindowsHookEx 失败,Win32 错误码 {Marshal.GetLastWin32Error()}");
    }

    /// <summary>先装新的(排到链首)再摘旧的:没有空档。装新的失败就保留旧的。只在钩子线程调用。</summary>
    private void Renew(bool quiet)
    {
        try
        {
            var old = _hook;
            _hook = NewHook();
            if (old != 0)
            {
                InputNative.UnhookWindowsHookEx(old); // 被系统静默摘掉的旧句柄会失败,无害
            }

            if (quiet)
            {
                Volatile.Write(ref _reasserts, _reasserts + 1);
            }
            else
            {
                _logger.LogInformation("键盘钩子已重新安装");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新安装键盘钩子失败");
        }
    }

    private void Uninstall()
    {
        if (_hook != 0)
        {
            InputNative.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint OnHook(int nCode, nuint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var start = Stopwatch.GetTimestamp();
            var consume = false;
            try
            {
                // KBDLLHOOKSTRUCT: vkCode @0, scanCode @4, flags @8, time @12, dwExtraInfo @16(x64)
                var vk = Marshal.ReadInt32(lParam, 0);
                var flags = Marshal.ReadInt32(lParam, 8);
                var time = unchecked((uint)Marshal.ReadInt32(lParam, 12));
                var extra = Marshal.ReadIntPtr(lParam, 16);
                var isDown = wParam is InputNative.WM_KEYDOWN or InputNative.WM_SYSKEYDOWN;
                var injected = (flags & InputNative.LLKHF_INJECTED) != 0;
                var ours = injected && extra == InputNative.Magic;
                if (!injected)
                {
                    Volatile.Write(ref _physical, _physical + 1); // 只有钩子线程写
                }

                consume = _pipeline.Process(new RawKey(vk, isDown, ours, time));
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _errors) <= MaxLoggedErrors)
                {
                    _logger.LogError(ex, "键盘钩子回调异常(已吞掉,按键放行)");
                }
            }

            Record(Stopwatch.GetTimestamp() - start);
            if (consume)
            {
                return 1;
            }
        }

        return InputNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // 只有钩子线程写,其他线程只读,用 Volatile 保证可见性
    private void Record(long ticks)
    {
        Volatile.Write(ref _events, _events + 1);
        Volatile.Write(ref _totalTicks, _totalTicks + ticks);
        if (ticks > _maxTicks)
        {
            Volatile.Write(ref _maxTicks, ticks);
        }
    }
}
