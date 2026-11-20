namespace Waio.Kit.Features;

/// <summary>
/// 功能的生命周期契约。实现类的构造函数只接收 <see cref="IFeatureContext"/>,不拿任何全局单例。
/// </summary>
public interface IFeature
{
    /// <summary>
    /// 启用:注册命令 handler、启动监听。抛异常时宿主会把该功能标记为 Failed 并隔离,不影响其他功能。
    /// </summary>
    ValueTask ActivateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 停用:必须释放全部资源(钩子过滤器、窗口、定时器、命令注册)。宿主保证会调用。
    /// </summary>
    ValueTask DeactivateAsync();
}
