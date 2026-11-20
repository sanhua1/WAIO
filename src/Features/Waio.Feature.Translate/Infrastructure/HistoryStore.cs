using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate.Infrastructure;

/// <summary>
/// 翻译历史(MAIO <c>HistoryStore</c>),保存在 <c>data\translate\history.json</c>。第一次使用时读取,之后在内存中维护,写盘在后台串行进行。
/// 运行中的功能和设置页的"查看历史"共用同一个实例(<see cref="For"/> 按文件路径缓存),不会互相覆盖。线程安全。
/// </summary>
internal sealed class HistoryStore
{
    private static readonly ConcurrentDictionary<string, HistoryStore> Instances = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private List<HistoryEntry> _entries = [];
    private bool _loaded;
    private Task _lastWrite = Task.CompletedTask;

    private HistoryStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>历史变化(添加、删除、清空)。可能在任意线程触发。</summary>
    public event Action? Changed;

    public static HistoryStore For(string dataDirectory, ILogger logger) =>
        Instances.GetOrAdd(Path.Combine(dataDirectory, "history.json"), path => new HistoryStore(path, logger));

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                Load();
                return [.. _entries];
            }
        }
    }

    public void Add(HistoryEntry entry, int limit) => Mutate(entries => HistoryList.Adding(entry, entries, limit));

    public void Remove(Guid id) => Mutate(entries => [.. entries.Where(e => e.Id != id)]);

    public void Clear() => Mutate(_ => []);

    /// <summary>等后台写盘完成(测试用)。</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            return _lastWrite;
        }
    }

    private void Mutate(Func<List<HistoryEntry>, List<HistoryEntry>> change)
    {
        lock (_gate)
        {
            Load();
            _entries = change(_entries);
            var snapshot = _entries.ToList();
            var previous = _lastWrite;
            _lastWrite = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                Write(snapshot);
            });
        }

        Changed?.Invoke();
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(_path))
            {
                _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path), Options)?
                    .Where(e => e is not null).ToList() ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "翻译历史读取失败,已忽略");
        }
    }

    private void Write(List<HistoryEntry> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "翻译历史保存失败");
        }
    }
}
