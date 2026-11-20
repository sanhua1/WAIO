using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Waio.Core.Storage;

/// <summary>
/// 配置文件的安全读写。两条铁律(ARCHITECTURE §7):
/// 1. 写入是原子的(先写临时文件再替换),崩溃或断电不会留下半个文件;
/// 2. 读到损坏的文件时,备份成 <c>.bad-时间戳</c>,返回"没有内容",由调用方用默认值启动——绝不因配置损坏而崩溃。
/// </summary>
public static class SafeJsonFile
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>读取文件文本。文件不存在返回 null。</summary>
    public static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>
    /// 读取并反序列化。文件不存在返回 default;内容损坏则备份原文件、记日志并返回 default(<paramref name="corrupted"/> 为 true)。
    /// </summary>
    public static T? TryRead<T>(string path, ILogger logger, out bool corrupted)
        where T : class
    {
        corrupted = false;
        string? text;
        try
        {
            text = ReadText(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "读取 {Path} 失败,按默认值处理", path);
            return null;
        }

        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException ex)
        {
            corrupted = true;
            var backup = BackupCorrupted(path, logger);
            logger.LogError(ex, "配置文件 {Path} 已损坏,已备份为 {Backup},将使用默认值", path, backup);
            return null;
        }
    }

    /// <summary>原子写入:同目录临时文件 + 覆盖移动。</summary>
    public static async Task WriteAtomicAsync(string path, string json, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>把损坏的文件改名成 .bad-时间戳,返回新路径;改名失败返回 null。</summary>
    public static string? BackupCorrupted(string path, ILogger logger)
    {
        var backup = $"{path}.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Move(path, backup);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "备份损坏文件 {Path} 失败", path);
            return null;
        }
    }
}
