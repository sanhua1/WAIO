using Waio.Kit.Commands;
using Waio.Kit.Features;

namespace Waio.Core.Host;

/// <summary>
/// 交给功能的受限命令注册器:只允许注册 manifest 里声明过的命令,并记录所有注册,功能停用时统一注销。
/// 这样"命令列表是静态可读的"这件事由宿主保证,设置页/绑定编辑器看到的清单不会和实际注册脱节。
/// </summary>
internal sealed class FeatureCommandRegistrar(FeatureManifest manifest, ICommandRegistrar inner) : ICommandRegistrar, IDisposable
{
    private readonly object _gate = new();
    private readonly List<IDisposable> _tokens = [];

    public IDisposable Register(CommandId id, Func<CommandArguments, CancellationToken, ValueTask> handler)
    {
        EnsureDeclared(id);
        return Track(inner.Register(id, handler));
    }

    public IDisposable Register<TInput, TOutput>(
        CommandContract<TInput, TOutput> contract,
        Func<TInput, CancellationToken, ValueTask<TOutput>> handler)
    {
        EnsureDeclared(contract.Id);
        return Track(inner.Register(contract, handler));
    }

    public void Dispose()
    {
        IDisposable[] tokens;
        lock (_gate)
        {
            tokens = [.. _tokens];
            _tokens.Clear();
        }

        foreach (var token in tokens)
        {
            token.Dispose();
        }
    }

    private void EnsureDeclared(CommandId id)
    {
        if (!manifest.Commands.Any(c => c.Id == id))
        {
            throw new InvalidOperationException(
                $"功能 '{manifest.Id}' 没有在清单(FeatureManifest.Commands)里声明命令 '{id}',不能注册它。");
        }
    }

    private IDisposable Track(IDisposable token)
    {
        lock (_gate)
        {
            _tokens.Add(token);
        }

        return token;
    }
}
