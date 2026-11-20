using Waio.Kit.Commands;

namespace Waio.Testing;

/// <summary>
/// 内存版命令总线(注册器 + 调用入口)。语义对齐真实总线的关键点:
/// 未注册 → Unavailable;重复注册 → 异常;释放 token → 注销。不做 JSON 参数转换(测试里直接传强类型)。
/// </summary>
public sealed class FakeCommandBus : ICommandRegistrar, ICommandBus
{
    private readonly Dictionary<CommandId, Delegate> _handlers = [];

    public IReadOnlyCollection<CommandId> RegisteredCommands => _handlers.Keys;

    public IDisposable Register(CommandId id, Func<CommandArguments, CancellationToken, ValueTask> handler) => Add(id, handler);

    public IDisposable Register<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        Func<TInput, CancellationToken, ValueTask<TOutput>> handler) => Add(contract.Id, handler);

    public async ValueTask PerformAsync(CommandId id, CommandArguments arguments, CancellationToken cancellationToken = default)
    {
        var handler = Find(id) as Func<CommandArguments, CancellationToken, ValueTask>
            ?? throw new CommandException(id, CommandErrorKind.InvalidArguments, $"命令 '{id}' 是契约命令,请用强类型调用");
        await handler(arguments, cancellationToken);
    }

    public async ValueTask<TOutput> PerformAsync<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        TInput input,
        CancellationToken cancellationToken = default)
    {
        var handler = Find(contract.Id) as Func<TInput, CancellationToken, ValueTask<TOutput>>
            ?? throw new CommandException(contract.Id, CommandErrorKind.InvalidArguments, $"命令 '{contract.Id}' 的签名与契约不符");
        return await handler(input, cancellationToken);
    }

    private IDisposable Add(CommandId id, Delegate handler)
    {
        if (!_handlers.TryAdd(id, handler))
        {
            throw new InvalidOperationException($"命令 '{id}' 已被注册");
        }

        return new Token(() => _handlers.Remove(id));
    }

    private Delegate Find(CommandId id) =>
        _handlers.TryGetValue(id, out var handler)
            ? handler
            : throw new CommandException(id, CommandErrorKind.Unavailable, $"命令 '{id}' 不可用");

    private sealed class Token(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
