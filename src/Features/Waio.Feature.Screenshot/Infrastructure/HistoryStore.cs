using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;

namespace Waio.Feature.Screenshot.Infrastructure;

/// <summary>
/// 截图历史的文件存储(MAIO ScreenshotHistoryStore,ADR-0012 的做法):<c>index.json</c> + 每条记录一个目录
/// (<c>original.png</c>、<c>thumb.png</c>;有标注后才有 <c>annotations.json</c>、<c>rendered.png</c>)。
/// 所有操作串行执行(MAIO 用 actor)。
/// </summary>
internal sealed class HistoryStore
{
    public const int ThumbnailPixels = 480;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScreenshotIndex _index;

    public HistoryStore(string directory, ILogger logger)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
        _logger = logger;
        _index = LoadIndex();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HistoryStore> Instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同一个目录只用一个实例(功能和设置页共用,否则两边各写各的 index.json 会互相覆盖)。</summary>
    public static HistoryStore For(string directory, ILogger logger) =>
        Instances.GetOrAdd(Path.GetFullPath(directory), path => new HistoryStore(path, logger));

    /// <summary>记录变化(新增、更新、删除、收藏)。在调用方的线程上触发,处理器要尽快返回。</summary>
    public event Action? Changed;

    /// <summary>要写入历史的文件。</summary>
    public sealed record Entry(byte[] Original, byte[] Thumbnail, int PixelWidth, int PixelHeight, double Scale)
    {
        /// <summary>带标注的最终图 PNG(没有标注时为 null)。</summary>
        public byte[]? Rendered { get; init; }

        /// <summary>标注对象 JSON。</summary>
        public byte[]? Annotations { get; init; }
    }

    public async Task<IReadOnlyList<ScreenshotRecord>> RecordsAsync() => await Locked(() => _index.Records.ToList());

    public string Folder(string id) => Path.Combine(_directory, id);

    /// <summary>最终图(有标注时是 rendered.png,否则 original.png)。</summary>
    public string FinalImagePath(ScreenshotRecord record) =>
        Path.Combine(Folder(record.Id), record.HasAnnotations ? "rendered.png" : "original.png");

    /// <summary>新增一条记录(最新的在前)。</summary>
    public Task<ScreenshotRecord> AddAsync(Entry entry, string? sourceApp, DateTimeOffset date) => Locked(() =>
    {
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var folder = Folder(id);
        Directory.CreateDirectory(folder);
        long bytes = 0;
        void Write(byte[] data, string name)
        {
            WriteAtomic(Path.Combine(folder, name), data);
            bytes += data.LongLength;
        }

        Write(entry.Original, "original.png");
        if (entry.Rendered is { } rendered)
        {
            Write(rendered, "rendered.png");
        }

        if (entry.Annotations is { } annotations)
        {
            Write(annotations, "annotations.json");
        }

        Write(entry.Thumbnail, "thumb.png");
        var record = new ScreenshotRecord
        {
            Id = id, CreatedAt = date, PixelWidth = entry.PixelWidth, PixelHeight = entry.PixelHeight, SourceApp = sourceApp,
            Bytes = bytes, HasAnnotations = entry.Annotations is not null, Scale = entry.Scale,
        };
        _index.Records.Insert(0, record);
        SaveIndex();
        Changed?.Invoke();
        return record;
    });

    public Task<ScreenshotRecord?> RecordAsync(string id) => Locked(() => _index.Records.FirstOrDefault(r => r.Id == id));

    public string OriginalPath(string id) => Path.Combine(Folder(id), "original.png");

    public string ThumbnailPath(string id) => Path.Combine(Folder(id), "thumb.png");

    /// <summary>标注对象(坐标相对截图左上角,物理像素);没有标注或读不了时为空。</summary>
    public IReadOnlyList<Domain.Annotation.Annotation> Annotations(string id)
    {
        try
        {
            var path = Path.Combine(Folder(id), "annotations.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<Domain.Annotation.Annotation>>(File.ReadAllBytes(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "读取截图标注失败:{Id}", id);
            return [];
        }
    }

    /// <summary>重新编辑标注后更新这条记录(原图不变),不新建记录(MAIO update,ADR-0012)。标注为空时删掉 rendered.png 和 annotations.json。</summary>
    public Task UpdateAsync(string id, byte[]? rendered, byte[]? annotations, byte[] thumbnail) => Locked(() =>
    {
        var position = _index.Records.FindIndex(r => r.Id == id);
        if (position < 0)
        {
            return;
        }

        var folder = Folder(id);
        foreach (var (data, name) in new[] { (rendered, "rendered.png"), (annotations, "annotations.json"), (thumbnail, "thumb.png") })
        {
            var path = Path.Combine(folder, name);
            if (data is not null)
            {
                WriteAtomic(path, data);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        _index.Records[position] = _index.Records[position] with
        {
            HasAnnotations = annotations is not null,
            Bytes = Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length),
        };
        SaveIndex();
        Changed?.Invoke();
    });

    /// <summary>收藏 / 取消收藏(收藏的不会被自动清理)。</summary>
    public Task SetPinnedAsync(string id, bool pinned) => Change(id, r => r with { Pinned = pinned });

    /// <summary>记下识别出的文字(历史按它搜索,MAIO setOCRText)。</summary>
    public Task SetOcrTextAsync(string id, string text) => Change(id, r => r with { OcrText = text });

    private Task Change(string id, Func<ScreenshotRecord, ScreenshotRecord> change) => Locked(() =>
    {
        var position = _index.Records.FindIndex(r => r.Id == id);
        if (position >= 0)
        {
            _index.Records[position] = change(_index.Records[position]);
            SaveIndex();
            Changed?.Invoke();
        }
    });

    public Task DeleteAsync(IReadOnlyCollection<string> ids) => Locked(() => Delete(ids));

    /// <summary>超过上限时删除最旧的(收藏的不删)。返回删除的条数。</summary>
    public Task<int> EnforceAsync(long? limit) => Locked(() =>
    {
        var ids = HistoryRetention.OverLimit(_index.Records, limit);
        Delete(ids);
        return ids.Count;
    });

    /// <summary>条数、总字节数、收藏条数(设置页显示占用)。</summary>
    public Task<(int Count, long Bytes, int Pinned)> StatsAsync() =>
        Locked(() => (_index.Records.Count, _index.Records.Sum(r => r.Bytes), _index.Records.Count(r => r.Pinned)));

    private void Delete(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var removing = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var id in removing)
        {
            try
            {
                Directory.Delete(Folder(id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "删除截图历史目录失败:{Id}", id);
            }
        }

        _index.Records.RemoveAll(r => removing.Contains(r.Id));
        SaveIndex();
        Changed?.Invoke();
    }

    private async Task<T> Locked<T>(Func<T> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Locked(Action action) => Locked(() =>
    {
        action();
        return 0;
    });

    private string IndexPath => Path.Combine(_directory, "index.json");

    private void SaveIndex() => WriteAtomic(IndexPath, JsonSerializer.SerializeToUtf8Bytes(_index, Json));

    private static void WriteAtomic(string path, byte[] data)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>读取索引。文件损坏时改名保留,并从各条记录的目录重建(只能恢复基本信息)。</summary>
    private ScreenshotIndex LoadIndex()
    {
        if (!File.Exists(IndexPath))
        {
            return Rebuild();
        }

        try
        {
            return JsonSerializer.Deserialize<ScreenshotIndex>(File.ReadAllBytes(IndexPath), Json) ?? Rebuild();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            var backup = Path.Combine(_directory, $"index.corrupt-{DateTimeOffset.Now.ToUnixTimeSeconds()}.json");
            _logger.LogWarning(ex, "截图历史索引损坏,已改名为 {Backup} 并从目录重建", Path.GetFileName(backup));
            try
            {
                File.Move(IndexPath, backup);
            }
            catch (IOException)
            {
                // 改名失败也照样重建
            }

            return Rebuild();
        }
    }

    private ScreenshotIndex Rebuild()
    {
        var records = new List<ScreenshotRecord>();
        foreach (var folder in Directory.EnumerateDirectories(_directory))
        {
            var original = new FileInfo(Path.Combine(folder, "original.png"));
            if (original.Exists)
            {
                records.Add(new ScreenshotRecord
                {
                    Id = Path.GetFileName(folder), CreatedAt = new DirectoryInfo(folder).CreationTime, Bytes = original.Length,
                });
            }
        }

        return new ScreenshotIndex { Records = [.. records.OrderByDescending(r => r.CreatedAt)] };
    }
}
