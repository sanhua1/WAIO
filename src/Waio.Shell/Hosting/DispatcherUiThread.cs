using Microsoft.UI.Dispatching;
using Waio.Kit.Services;

namespace Waio.Shell.Hosting;

/// <summary><see cref="IUiThread"/> 的 WinUI 实现:包一层 DispatcherQueue。</summary>
internal sealed class DispatcherUiThread(DispatcherQueue queue) : IUiThread
{
    public bool IsCurrent => queue.HasThreadAccess;

    public bool Post(Action action) => queue.TryEnqueue(() => action());
}
