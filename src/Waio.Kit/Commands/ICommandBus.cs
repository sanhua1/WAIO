namespace Waio.Kit.Commands;

/// <summary>命令的注册入口。每个功能拿到的是只能注册自己 manifest 所声明命令的受限实例。</summary>
public interface ICommandRegistrar
{
    /// <summary>注册普通命令(参数来自用户绑定,弱类型)。返回的 token 释放即注销。</summary>
    IDisposable Register(CommandId id, Func<CommandArguments, CancellationToken, ValueTask> handler);

    /// <summary>注册契约命令(强类型,供其他功能调用)。</summary>
    IDisposable Register<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        Func<TInput, CancellationToken, ValueTask<TOutput>> handler);
}

/// <summary>命令的调用入口。触发器(快捷键、托盘菜单、URL 协议)和功能间协作都走这里。</summary>
public interface ICommandBus
{
    /// <summary>
    /// 按 id 执行命令。命令不存在(功能未启用)抛 <see cref="CommandException"/>(Unavailable)。
    /// 若目标是契约命令,会把 <paramref name="arguments"/> 反序列化成契约的输入类型。
    /// </summary>
    ValueTask PerformAsync(CommandId id, CommandArguments arguments, CancellationToken cancellationToken = default);

    /// <summary>执行契约命令(进程内直接调用,不经过 JSON)。</summary>
    ValueTask<TOutput> PerformAsync<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        TInput input,
        CancellationToken cancellationToken = default);
}
