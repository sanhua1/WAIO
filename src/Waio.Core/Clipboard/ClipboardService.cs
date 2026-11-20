using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Waio.Kit.Services;
using static Waio.Core.Clipboard.ClipboardNative;

namespace Waio.Core.Clipboard;

/// <summary>
/// 系统剪贴板服务:专用线程 + 隐藏消息窗口 + AddClipboardFormatListener。
/// 所有剪贴板读写都在这个线程上串行执行(Win32 剪贴板同一时刻只能被一个窗口打开)。
/// 自己写入的内容靠 <c>GetClipboardSequenceNumber</c> 识别并跳过,不会"收到自己"。
/// </summary>
public sealed class ClipboardService : IClipboardService, IDisposable
{
    private const string ClassName = "WAIO.ClipboardWindow";
    private const uint WmSetRequest = WmApp + 1;
    private const int OpenAttempts = 20; // 其他程序正占着剪贴板很常见,短暂重试
    private const int OpenRetryMilliseconds = 15;

    /// <summary>投递给剪贴板线程的工作:写入或读取。Fail 在服务关闭、来不及执行时调用。</summary>
    private sealed record Job(Action Run, Action Fail);

    private readonly ILogger _logger;
    private readonly WndProc _wndProc; // 持有引用,防止被 GC 回收
    private readonly ConcurrentQueue<Job> _requests = new();
    private readonly ManualResetEventSlim _ready = new();

    private Thread? _thread;
    private nint _hwnd;
    private Exception? _startError;
    private uint _selfSequence;
    private volatile bool _respectExclusion = true;
    private int _suppressDepth;
    private uint _suppressThrough; // 序号 ≤ 它的变化都不通知(SuppressChanges 释放时记下)

    public ClipboardService(ILogger logger)
    {
        _logger = logger;
        _wndProc = OnMessage;
    }

    public event Action<ClipboardChange>? Changed;

    event Action<ClipboardChange> IClipboardService.Changed
    {
        add => Changed += value;
        remove => Changed -= value;
    }

    public bool RespectExclusionFormats
    {
        get => _respectExclusion;
        set => _respectExclusion = value;
    }

    /// <summary>启动监听线程。创建窗口或注册监听失败会抛异常。</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "WAIO.Clipboard" };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            throw new InvalidOperationException("启动剪贴板监听失败", _startError);
        }
    }

    public ValueTask<bool> SetAsync(ClipboardContent content, CancellationToken cancellationToken = default) =>
        WriteAsync(content, markSelf: true);

    /// <summary>不记成"自己写的":随后的 WM_CLIPBOARDUPDATE 照常读出并触发 Changed,剪贴板历史因此会记录(ADR-0015)。</summary>
    public ValueTask<bool> CopyAsync(ClipboardContent content, CancellationToken cancellationToken = default) =>
        WriteAsync(content, markSelf: false);

    private ValueTask<bool> WriteAsync(ClipboardContent content, bool markSelf)
    {
        if (_hwnd == 0)
        {
            return ValueTask.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new Job(() => completion.TrySetResult(SafeWrite(content, markSelf)), () => completion.TrySetResult(false)));
        return new ValueTask<bool>(completion.Task);
    }

    public uint ChangeCount => GetClipboardSequenceNumber();

    public ValueTask<ClipboardContent?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_hwnd == 0)
        {
            return ValueTask.FromResult<ClipboardContent?>(null);
        }

        var completion = new TaskCompletionSource<ClipboardContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new Job(() => completion.TrySetResult(SafeRead()), () => completion.TrySetResult(null)));
        return new ValueTask<ClipboardContent?>(completion.Task);
    }

    public IDisposable SuppressChanges()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new Suppression(this);
    }

    private sealed class Suppression(ClipboardService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // 目标程序写剪贴板后,WM_CLIPBOARDUPDATE 是异步到达的:释放之前已经发生的写入也不通知
                Volatile.Write(ref owner._suppressThrough, GetClipboardSequenceNumber());
                Interlocked.Decrement(ref owner._suppressDepth);
            }
        }
    }

    private void Enqueue(Job job)
    {
        _requests.Enqueue(job);
        PostMessageW(_hwnd, WmSetRequest, 0, 0);
    }

    public void Dispose()
    {
        if (_thread is null)
        {
            return;
        }

        PostMessageW(_hwnd, WmClose, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        while (_requests.TryDequeue(out var pending))
        {
            pending.Fail(); // 关闭时还没处理的请求:告知失败,别让调用方一直等
        }
    }

    private void Run()
    {
        var instance = GetModuleHandleW(null);
        try
        {
            var wndClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = instance,
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref wndClass) == 0 && Marshal.GetLastWin32Error() != 1410 /* 类已存在 */)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "注册剪贴板窗口类失败");
            }

            _hwnd = CreateWindowExW(0, ClassName, "WAIO Clipboard", 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
            if (_hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "创建剪贴板消息窗口失败");
            }

            if (!AddClipboardFormatListener(_hwnd))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "注册剪贴板监听失败");
            }
        }
        catch (Win32Exception ex)
        {
            _startError = ex;
        }
        finally
        {
            _ready.Set();
        }

        if (_startError is not null)
        {
            return;
        }

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        UnregisterClassW(ClassName, instance);
    }

    private nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmClipboardUpdate:
                SafeHandleUpdate();
                return 0;
            case WmSetRequest:
                DrainSetRequests();
                return 0;
            case WmClose:
                RemoveClipboardFormatListener(hwnd);
                DestroyWindow(hwnd);
                return 0;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    // 窗口过程里的异常会让整个进程崩溃,所以任何东西都不能漏出去
    private void SafeHandleUpdate()
    {
        try
        {
            HandleUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理剪贴板变化时出错(已忽略)");
        }
    }

    private void HandleUpdate()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _selfSequence)
        {
            return; // WAIO 自己刚写入的
        }

        if (Volatile.Read(ref _suppressDepth) > 0 || unchecked((int)(sequence - Volatile.Read(ref _suppressThrough))) <= 0)
        {
            return; // 有功能在临时借用剪贴板(SuppressChanges),这次变化不是用户的复制
        }

        ClipboardContent? content;
        var tooLarge = false;
        if (!TryOpen())
        {
            _logger.LogWarning("剪贴板一直被占用,放弃读取这次变化");
            return;
        }

        try
        {
            if (_respectExclusion && ClipboardFormatsIO.IsMarkedAsExcluded())
            {
                return; // 来源程序(如密码管理器)声明了不要记录
            }

            content = ClipboardFormatsIO.Read(out tooLarge);
        }
        finally
        {
            CloseClipboard(); // 先关再通知:不能在持有剪贴板时调用外部处理器
        }

        if (tooLarge)
        {
            _logger.LogInformation("剪贴板内容超过 {Limit} MB,未记录", ClipboardFormatsIO.MaxTotalBytes / 1024 / 1024);
            return;
        }

        if (content is null || content.IsEmpty)
        {
            return;
        }

        Changed?.Invoke(new ClipboardChange(content, GetOwnerProcessName()));
    }

    private void DrainSetRequests()
    {
        while (_requests.TryDequeue(out var job))
        {
            job.Run();
        }
    }

    private bool SafeWrite(ClipboardContent content, bool markSelf)
    {
        try
        {
            return Write(content, markSelf);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入剪贴板时出错");
            return false;
        }
    }

    private ClipboardContent? SafeRead()
    {
        try
        {
            if (!TryOpen())
            {
                _logger.LogWarning("剪贴板一直被占用,读取失败");
                return null;
            }

            try
            {
                var content = ClipboardFormatsIO.Read(out var tooLarge);
                return tooLarge ? null : content;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取剪贴板时出错");
            return null;
        }
    }

    private bool Write(ClipboardContent content, bool markSelf)
    {
        if (!TryOpen())
        {
            _logger.LogWarning("剪贴板一直被占用,写入失败");
            return false;
        }

        try
        {
            return ClipboardFormatsIO.Write(content);
        }
        finally
        {
            CloseClipboard();
            if (markSelf)
            {
                _selfSequence = GetClipboardSequenceNumber();
            }
        }
    }

    private bool TryOpen()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(_hwnd))
            {
                return true;
            }

            Thread.Sleep(OpenRetryMilliseconds);
        }

        return false;
    }

    private static string? GetOwnerProcessName()
    {
        var owner = GetClipboardOwner();
        if (owner == 0 || GetWindowThreadProcessId(owner, out var pid) == 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var name = new StringBuilder(512);
            var size = (uint)name.Capacity;
            return QueryFullProcessImageNameW(process, 0, name, ref size)
                ? Path.GetFileNameWithoutExtension(name.ToString())
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
