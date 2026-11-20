using Microsoft.Extensions.Logging;
using Waio.Kit.Features;

namespace Waio.Core.Host;

/// <summary>
/// 功能宿主:负责每个功能的生命周期与故障隔离(ARCHITECTURE §8)。
/// <list type="bullet">
/// <item>某个功能创建/激活失败或超时 → 标记 Failed 并清理,<b>绝不影响其他功能和宿主</b>;</item>
/// <item>运行条件不满足(如缺管理员)→ NeedsAttention,不启动;</item>
/// <item>停用时无论功能自己有没有释放,宿主都会注销它的命令、过滤器和键盘会话。</item>
/// </list>
/// 所有状态转换串行执行。
/// </summary>
public sealed class FeatureHost : IAsyncDisposable
{
    public static readonly TimeSpan DefaultActivationTimeout = TimeSpan.FromSeconds(15);

    private sealed record Active(IFeature Feature, FeatureContext Context);

    private readonly IReadOnlyDictionary<FeatureId, FeatureDescriptor> _catalog;
    private readonly HostServices _services;
    private readonly ICapabilityProbe _probe;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _activationTimeout;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly Dictionary<FeatureId, FeatureStatus> _statuses = [];
    private readonly Dictionary<FeatureId, Active> _active = [];

    public FeatureHost(
        IReadOnlyList<FeatureDescriptor> catalog,
        HostServices services,
        ICapabilityProbe probe,
        ILoggerFactory loggerFactory,
        TimeSpan? activationTimeout = null)
    {
        ValidateCatalog(catalog);
        _catalog = catalog.ToDictionary(d => d.Manifest.Id);
        _services = services;
        _probe = probe;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<FeatureHost>();
        _activationTimeout = activationTimeout ?? DefaultActivationTimeout;

        foreach (var descriptor in catalog)
        {
            _statuses[descriptor.Manifest.Id] = new FeatureStatus(descriptor.Manifest.Id, FeatureState.Disabled);
        }
    }

    public event Action<FeatureStatus>? StatusChanged;

    public IReadOnlyList<FeatureStatus> Statuses
    {
        get
        {
            lock (_statusLock)
            {
                return [.. _statuses.Values];
            }
        }
    }

    public FeatureStatus GetStatus(FeatureId id)
    {
        lock (_statusLock)
        {
            return _statuses.TryGetValue(id, out var status)
                ? status
                : throw new KeyNotFoundException($"目录里没有功能 '{id}'");
        }
    }

    /// <summary>按顺序激活给定的功能。单个功能失败只记录,不中断后续功能。</summary>
    public async Task StartAsync(IEnumerable<FeatureId> enabled, CancellationToken cancellationToken = default)
    {
        foreach (var id in enabled)
        {
            await ActivateAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ActivateAsync(FeatureId id, CancellationToken cancellationToken = default)
    {
        var descriptor = Find(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ActivateCoreAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeactivateAsync(FeatureId id)
    {
        Find(id);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_active.Remove(id, out var active))
            {
                await CleanupAsync(active.Feature, active.Context).ConfigureAwait(false);
            }

            SetStatus(new FeatureStatus(id, FeatureState.Disabled));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停用全部运行中的功能(按激活的逆序)。</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in _active.Keys.Reverse().ToList())
            {
                var active = _active[id];
                _active.Remove(id);
                await CleanupAsync(active.Feature, active.Context).ConfigureAwait(false);
                SetStatus(new FeatureStatus(id, FeatureState.Disabled));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ActivateCoreAsync(FeatureDescriptor descriptor, CancellationToken cancellationToken)
    {
        var manifest = descriptor.Manifest;
        if (_active.ContainsKey(manifest.Id))
        {
            return;
        }

        var check = _probe.Check(manifest.RequiredCapabilities);
        if (!check.Satisfied)
        {
            _logger.LogWarning("功能 {Feature} 运行条件不满足:{Reason}", manifest.Id, check.Reason);
            SetStatus(new FeatureStatus(manifest.Id, FeatureState.NeedsAttention, check.Reason));
            return;
        }

        FeatureContext? context = null;
        IFeature? feature = null;
        using var activationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            context = new FeatureContext(manifest, _services, _loggerFactory);
            feature = descriptor.Create(context);

            var activation = feature.ActivateAsync(activationCts.Token).AsTask();
            try
            {
                await activation.WaitAsync(_activationTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                activationCts.Cancel();
                _ = activation.ContinueWith(
                    static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // 避免未观察异常
                throw;
            }

            _active[manifest.Id] = new Active(feature, context);
            _logger.LogInformation("功能 {Feature} 已启动", manifest.Id);
            SetStatus(new FeatureStatus(manifest.Id, FeatureState.Running));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAsync(feature, context).ConfigureAwait(false);
            SetStatus(new FeatureStatus(manifest.Id, FeatureState.Disabled));
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is TimeoutException
                ? $"激活超过 {_activationTimeout.TotalSeconds:0.#} 秒仍未完成,已放弃"
                : ex.Message;
            _logger.LogError(ex, "功能 {Feature} 启动失败,已隔离", manifest.Id);
            await CleanupAsync(feature, context).ConfigureAwait(false);
            SetStatus(new FeatureStatus(manifest.Id, FeatureState.Failed, message, ex));
        }
    }

    /// <summary>先让功能自己停用(带超时、吞异常),再由宿主兜底释放它申请过的全部资源。</summary>
    private async Task CleanupAsync(IFeature? feature, FeatureContext? context)
    {
        if (feature is not null)
        {
            try
            {
                await feature.DeactivateAsync().AsTask().WaitAsync(_activationTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "功能停用时出错(已忽略,宿主继续释放其资源)");
            }
        }

        if (context is not null)
        {
            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放功能资源时出错");
            }
        }
    }

    private FeatureDescriptor Find(FeatureId id) =>
        _catalog.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"目录里没有功能 '{id}'");

    private void SetStatus(FeatureStatus status)
    {
        lock (_statusLock)
        {
            _statuses[status.Id] = status;
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StatusChanged 订阅者抛出异常(已忽略)");
        }
    }

    private static void ValidateCatalog(IReadOnlyList<FeatureDescriptor> catalog)
    {
        var ids = new HashSet<FeatureId>();
        foreach (var manifest in catalog.Select(d => d.Manifest))
        {
            if (!ids.Add(manifest.Id))
            {
                throw new ArgumentException($"功能 id '{manifest.Id}' 重复登记", nameof(catalog));
            }

            var commandIds = new HashSet<Kit.Commands.CommandId>();
            foreach (var command in manifest.Commands)
            {
                if (command.Id.FeaturePart != manifest.Id.Value)
                {
                    throw new ArgumentException(
                        $"功能 '{manifest.Id}' 声明了不属于它的命令 '{command.Id}'(命令 id 必须以功能 id 开头)", nameof(catalog));
                }

                if (!commandIds.Add(command.Id))
                {
                    throw new ArgumentException($"功能 '{manifest.Id}' 重复声明了命令 '{command.Id}'", nameof(catalog));
                }
            }

            foreach (var binding in manifest.DefaultBindings)
            {
                if (!commandIds.Contains(binding.Command))
                {
                    throw new ArgumentException(
                        $"功能 '{manifest.Id}' 的出厂绑定引用了未声明的命令 '{binding.Command}'", nameof(catalog));
                }
            }
        }
    }
}
