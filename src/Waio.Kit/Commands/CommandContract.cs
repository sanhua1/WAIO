namespace Waio.Kit.Commands;

/// <summary>
/// 类型安全的跨功能契约。双方只依赖契约对象,不互相引用(编译器会拒绝功能间引用)。
/// 契约统一定义在 Waio.Kit.Contracts 下,例如:
/// <code>
/// public static class TranslateContracts
/// {
///     public static readonly CommandContract&lt;TranslateTextInput, Unit&gt; TranslateText = new("translate.text");
/// }
/// </code>
/// 提供方 <c>ctx.Commands.Register(TranslateContracts.TranslateText, handler)</c>,
/// 调用方 <c>ctx.CommandBus.PerformAsync(TranslateContracts.TranslateText, input)</c>。
/// </summary>
public sealed record CommandContract<TInput, TOutput>(CommandId Id);
