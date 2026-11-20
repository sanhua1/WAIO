using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Waio.Kit.Services;

namespace Waio.Core.Platform;

/// <summary>
/// 前台窗口跟踪(<see cref="ISystemEvents"/> 的真实实现)。专用线程 + 消息循环,
/// 用 WinEvent 钩子被动接收前台变化,把"当前前台进程"维护成原子快照,
/// 输入钩子线程直接读取,不用在回调里调用任何慢的系统 API(ARCHITECTURE §5.2 第 6 条)。
/// </summary>
public sealed class ForegroundTracker : ISystemEvents, IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_QUIT = 0x0012;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly ILogger _logger;
    private readonly WinEventProc _callback; // 持有引用防 GC
    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private ForegroundWindowInfo? _foreground;

    public ForegroundTracker(ILogger logger)
    {
        _logger = logger;
        _callback = OnEvent;
    }

    public ForegroundWindowInfo? Foreground => Volatile.Read(ref _foreground);

    public event Action<ForegroundWindowInfo>? ForegroundChanged;

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WAIO.ForegroundTracker" };
        _thread.Start();
        _started.Wait();
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, 0, 0x0400, 0x0400, 0);
        try
        {
            Update(GetForegroundWindow()); // 初始状态
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (_hook == 0)
            {
                _logger.LogWarning("SetWinEventHook 失败,前台窗口信息将不会更新");
            }
        }
        finally
        {
            _started.Set();
        }

        while (GetMessageW(out _, 0, 0, 0) > 0)
        {
            // 只需要消息循环让 WinEvent 回调得以分发
        }

        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
        }
    }

    private void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time) =>
        Update(hwnd);

    private void Update(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var info = new ForegroundWindowInfo(hwnd, ProcessNameOf(pid), TitleOf(hwnd));
            Volatile.Write(ref _foreground, info);
            ForegroundChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新前台窗口信息失败");
        }
    }

    private static string ProcessNameOf(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
        {
            return string.Empty; // 无权限(如受保护进程)
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? Path.GetFileNameWithoutExtension(buffer.ToString())
                : string.Empty;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string TitleOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetWindowTextW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Msg msg, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }
}
