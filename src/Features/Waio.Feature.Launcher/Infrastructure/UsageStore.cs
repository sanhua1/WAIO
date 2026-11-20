using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waio.Feature.Launcher.Domain;

namespace Waio.Feature.Launcher.Infrastructure;

/// <summary>
/// 常用度存盘:data/launcher/usage.json。写临时文件再改名(不会写出半个文件);
/// 读不出来(损坏)就改名备份成 .bad-时间,从空的开始——常用度丢了不影响使用,不值得因此启动失败。
/// </summary>
internal sealed class UsageStore(string dataDirectory, ILogger logger)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    private string FilePath => Path.Combine(dataDirectory, "usage.json");

    public UsageBook Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UsageBook();
            }

            return JsonSerializer.Deserialize<UsageBook>(File.ReadAllText(FilePath), Options) ?? new UsageBook();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "启动台常用记录读不出来,备份后从空的开始");
            TryBackup();
            return new UsageBook();
        }
    }

    /// <summary>保存一份快照(调用方在 UI 线程序列化,写盘在后台)。</summary>
    public void SaveInBackground(UsageBook book)
    {
        var json = JsonSerializer.Serialize(book, Options);
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(dataDirectory);
                    var temp = FilePath + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, FilePath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "保存启动台常用记录失败");
                }
            }
        });
    }

    private void TryBackup()
    {
        try
        {
            File.Move(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "备份损坏的常用记录失败");
        }
    }
}
