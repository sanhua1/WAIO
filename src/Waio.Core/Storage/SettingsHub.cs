using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Waio.Kit.Features;
using Waio.Kit.Services;

namespace Waio.Core.Storage;

/// <summary>
/// 按功能 id 分发(并缓存)<see cref="FeatureSettings"/> 与 <see cref="SecretStore"/> 实例,保证同一功能只有一个写入者
/// ——运行中的功能和设置窗口里的设置页拿到的是同一个实例。
/// </summary>
public sealed class SettingsHub(AppPaths paths, ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<FeatureId, FeatureSettings> _byFeature = new();
    private readonly ConcurrentDictionary<FeatureId, SecretStore> _secrets = new();

    public IFeatureSettings ForFeature(FeatureId id) =>
        _byFeature.GetOrAdd(id, key => new FeatureSettings(key, paths, loggerFactory.CreateLogger($"settings.{key}")));

    public ISecretStore SecretsFor(FeatureId id) =>
        _secrets.GetOrAdd(id, key => new SecretStore(
            Path.Combine(paths.FeatureDataDirectory(key), "secrets.json"), loggerFactory.CreateLogger($"secrets.{key}")));
}
