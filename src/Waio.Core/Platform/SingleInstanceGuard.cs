using System.Security.AccessControl;
using System.Security.Principal;

namespace Waio.Core.Platform;

/// <summary>
/// 单实例守卫(ADR-0004 §4)。第一个实例持有互斥体并监听"激活请求"事件;之后启动的实例发现已有实例,
/// 发出激活请求(例如"显示设置窗口")后退出。
/// <para>
/// 常驻实例是管理员权限,而用户双击启动的可能是普通权限:提权进程创建的内核对象默认只有管理员能打开,
/// 所以事件显式授权给<b>当前用户</b>(只给 Modify|Synchronize,不能改 ACL)。
/// 能触发的只是"显示设置窗口",即使被其他进程误触发也无害。
/// </para>
/// <para>
/// ⚠️ 未验证:没有显式设置对象的完整性标签,"普通权限实例 → 提权实例"的通知在真机上是否一定成功,
/// 需要实机测试;失败时 <see cref="SignalExisting"/> 返回 false,调用方应给出提示而不是静默退出。
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _listener;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
        _listener = new Thread(Listen) { IsBackground = true, Name = "WAIO.SingleInstance" };
        _listener.Start();
    }

    /// <summary>已有实例请求激活时触发(在后台线程上,UI 代码自行切回 UI 线程)。</summary>
    public event Action? ActivationRequested;

    /// <summary>尝试成为主实例。已有实例在运行返回 null(此时可调用 <see cref="SignalExisting"/>)。</summary>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        // 不"拥有"互斥体(initiallyOwned: false):Mutex 只能由拥有它的线程释放,而我们可能在任意线程 Dispose。
        // 只靠 createdNew 判断是不是第一个创建者,并保持句柄打开直到进程退出即可。
        var mutex = new Mutex(initiallyOwned: false, MutexName(name), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        try
        {
            return new SingleInstanceGuard(mutex, CreateActivationEvent(name));
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>通知已运行的实例。打不开事件(权限不足、实例刚退出)返回 false。</summary>
    public static bool SignalExisting(string name)
    {
        try
        {
            using var existing = EventWaitHandleAcl.OpenExisting(EventName(name), EventWaitHandleRights.Modify);
            return existing.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _activation.Set(); // 唤醒监听线程让它退出
        _listener.Join(TimeSpan.FromSeconds(1));
        _activation.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }

    private void Listen()
    {
        while (true)
        {
            _activation.WaitOne();
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            ActivationRequested?.Invoke();
        }
    }

    private static EventWaitHandle CreateActivationEvent(string name)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法获取当前用户 SID");
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            user, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));

        var handle = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, EventName(name), out _, security);
        return handle;
    }

    private static string MutexName(string name) => $@"Local\{name}.Mutex";

    private static string EventName(string name) => $@"Local\{name}.Activate";
}
