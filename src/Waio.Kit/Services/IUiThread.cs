namespace Waio.Kit.Services;

/// <summary>
/// 把工作投递到 UI 线程。需要窗口的功能(剪贴板面板、启动台)用它创建和操作 WinUI 对象——
/// 这些对象只能在 UI 线程上创建。Kit 平台无关,所以这里只暴露最小的抽象,由 Shell 用 DispatcherQueue 实现。
/// </summary>
public interface IUiThread
{
    /// <summary>当前是否就在 UI 线程。</summary>
    bool IsCurrent { get; }

    /// <summary>排队到 UI 线程执行。关闭阶段可能被丢弃(返回 false)。</summary>
    bool Post(Action action);
}
