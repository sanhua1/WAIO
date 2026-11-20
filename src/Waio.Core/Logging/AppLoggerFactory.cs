using Microsoft.Extensions.Logging;

namespace Waio.Core.Logging;

/// <summary>
/// 最小的 <see cref="ILoggerFactory"/>:所有日志器都交给同一个 provider。
/// <c>LoggerFactory</c> 类在完整的 Microsoft.Extensions.Logging 包里,为它新增依赖不值得(ADR-0007)。
/// </summary>
public sealed class AppLoggerFactory(ILoggerProvider provider) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

    /// <summary>不支持再添加 provider:本工厂只有一个目的地。</summary>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("AppLoggerFactory 只使用构造时给定的 provider");

    public void Dispose() => provider.Dispose();
}
