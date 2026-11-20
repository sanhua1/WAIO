using System.Collections.Concurrent;
using System.Text.Json;
using Waio.Kit.Commands;

namespace Waio.Core.Commands;

/// <summary>
/// 命令总线实现:id → handler 的注册表。同时承担 <see cref="ICommandRegistrar"/> 与 <see cref="ICommandBus"/>。
/// 线程安全。handler 抛出的业务异常统一包装成 <see cref="CommandException"/>(Failed)。
/// </summary>
public sealed class CommandBus : ICommandRegistrar, ICommandBus
{
    private sealed record Entry(Type InputType, Func<object?, CancellationToken, ValueTask<object?>> Invoke);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<CommandId, Entry> _entries = new();

    public bool IsRegistered(CommandId id) => _entries.ContainsKey(id);

    public IDisposable Register(CommandId id, Func<CommandArguments, CancellationToken, ValueTask> handler) =>
        Add(id, new Entry(typeof(CommandArguments), async (input, ct) =>
        {
            await handler((CommandArguments)input!, ct).ConfigureAwait(false);
            return null;
        }));

    public IDisposable Register<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        Func<TInput, CancellationToken, ValueTask<TOutput>> handler) =>
        Add(contract.Id, new Entry(typeof(TInput), async (input, ct) =>
            await handler((TInput)input!, ct).ConfigureAwait(false)));

    public async ValueTask PerformAsync(CommandId id, CommandArguments arguments, CancellationToken cancellationToken = default)
    {
        var entry = Find(id);
        object? input = entry.InputType == typeof(CommandArguments)
            ? arguments
            : DeserializeArguments(id, entry.InputType, arguments);
        await InvokeAsync(id, entry, input, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TOutput> PerformAsync<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        TInput input,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(contract.Id);
        if (entry.InputType != typeof(TInput))
        {
            throw new CommandException(contract.Id, CommandErrorKind.InvalidArguments,
                $"命令 '{contract.Id}' 的输入类型是 {entry.InputType.Name},调用方传入的是 {typeof(TInput).Name}");
        }

        var result = await InvokeAsync(contract.Id, entry, input, cancellationToken).ConfigureAwait(false);
        return (TOutput)result!;
    }

    private IDisposable Add(CommandId id, Entry entry)
    {
        if (!_entries.TryAdd(id, entry))
        {
            throw new InvalidOperationException($"命令 '{id}' 已被注册。每个命令只能有一个提供方。");
        }

        return new Registration(() => _entries.TryRemove(new KeyValuePair<CommandId, Entry>(id, entry)));
    }

    private Entry Find(CommandId id) =>
        _entries.TryGetValue(id, out var entry)
            ? entry
            : throw new CommandException(id, CommandErrorKind.Unavailable, $"命令 '{id}' 不可用(提供它的功能未启用或已故障)");

    private static async ValueTask<object?> InvokeAsync(CommandId id, Entry entry, object? input, CancellationToken ct)
    {
        try
        {
            return await entry.Invoke(input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消是正常控制流,不包装
        }
        catch (CommandException)
        {
            throw; // 嵌套调用已经包装过
        }
        catch (Exception ex)
        {
            throw new CommandException(id, CommandErrorKind.Failed, $"命令 '{id}' 执行失败:{ex.Message}", ex);
        }
    }

    private static object DeserializeArguments(CommandId id, Type inputType, CommandArguments arguments)
    {
        try
        {
            var json = JsonSerializer.Serialize(arguments.Values);
            return JsonSerializer.Deserialize(json, inputType, JsonOptions)
                   ?? throw new JsonException("反序列化结果为 null");
        }
        catch (JsonException ex)
        {
            throw new CommandException(id, CommandErrorKind.InvalidArguments,
                $"命令 '{id}' 的参数无法转换成 {inputType.Name}:{ex.Message}", ex);
        }
    }

    private sealed class Registration(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
