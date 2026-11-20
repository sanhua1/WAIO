using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Waio.Feature.Launcher.Infrastructure;

/// <summary>
/// 一个专用的单线程套间(STA)后台线程,按顺序执行排队的工作。
/// Shell 的文件夹和图标接口要在 STA 里调用;放在 UI 线程会卡界面,放在线程池(MTA)有的 Shell 扩展不工作。
/// </summary>
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>排队执行;结果或异常通过返回的任务给出。已经停止时任务直接取消。</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetCanceled(); // 已经 Dispose
        }

        return completion.Task;
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Run()
    {
        _ = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }
        finally
        {
            CoUninitialize();
            _queue.Dispose();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
