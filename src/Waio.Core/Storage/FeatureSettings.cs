using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waio.Kit.Features;
using Waio.Kit.Services;

namespace Waio.Core.Storage;

/// <summary>
/// 单个功能的设置(features/&lt;featureId&gt;.json)。
/// 缓存的是 JSON 文本而不是对象:每次 <see cref="Get{T}"/> 返回独立副本,
/// 调用方改了返回值但没保存,不会污染别人看到的设置。
/// </summary>
public sealed class FeatureSettings : IFeatureSettings
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _json;
    private bool _loaded;

    public FeatureSettings(FeatureId id, AppPaths paths, ILogger logger)
    {
        _path = paths.FeatureSettingsFile(id);
        _logger = logger;
    }

    public event Action? Changed;

    public T Get<T>()
        where T : class, new()
    {
        string? json;
        lock (_gate)
        {
            EnsureLoaded<T>();
            json = _json;
        }

        if (json is null)
        {
            return new T();
        }

        // 缓存里的文本在 EnsureLoaded 或 SaveAsync 时已经验证过能反序列化成 T;
        // 若调用方换了类型 T,这里可能失败,按默认值处理。
        try
        {
            return JsonSerializer.Deserialize<T>(json, SafeJsonFile.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "设置内容与类型 {Type} 不匹配,使用默认值", typeof(T).Name);
            return new T();
        }
    }

    public async ValueTask SaveAsync<T>(T value, CancellationToken cancellationToken = default)
        where T : class, new()
    {
        var json = SafeJsonFile.Serialize(value);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SafeJsonFile.WriteAtomicAsync(_path, json, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _json = json;
                _loaded = true;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke();
    }

    private void EnsureLoaded<T>()
        where T : class
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var value = SafeJsonFile.TryRead<T>(_path, _logger, out _);
        _json = value is null ? null : SafeJsonFile.Serialize(value);
    }
}
