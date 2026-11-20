using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waio.Core.Storage;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

/// <param name="Bindings">成功解析的绑定。</param>
/// <param name="Errors">被拒绝的条目及原因,供设置页提示用户。</param>
public sealed record BindingLoadResult(IReadOnlyList<CommandBinding> Bindings, IReadOnlyList<string> Errors);

/// <summary>
/// 读写 bindings.json。配置是不可信输入(ADR-0004):
/// 单条绑定不合法只丢弃该条并报告,其余照常生效;只要有丢弃,先把原文件备份,保存时才不会悄悄丢用户数据;
/// 整个文件不是合法 JSON 则备份并以空列表启动。
/// </summary>
public sealed class BindingStore(AppPaths paths, ILogger logger)
{
    public BindingLoadResult Load()
    {
        var path = paths.BindingsFile;
        string? text;
        try
        {
            text = SafeJsonFile.ReadText(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "读取 {Path} 失败", path);
            return new BindingLoadResult([], [$"读取绑定文件失败:{ex.Message}"]);
        }

        if (text is null)
        {
            return new BindingLoadResult([], []);
        }

        BindingsFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<BindingsFileDto>(text, SafeJsonFile.Options);
        }
        catch (JsonException ex)
        {
            var backup = SafeJsonFile.BackupCorrupted(path, logger);
            logger.LogError(ex, "绑定文件已损坏,备份为 {Backup}", backup);
            return new BindingLoadResult([], [$"绑定文件不是合法的 JSON,已备份为 {backup}:{ex.Message}"]);
        }

        var bindings = new List<CommandBinding>();
        var errors = new List<string>();
        var seen = new HashSet<Guid>();

        foreach (var (dto, index) in (file?.Bindings ?? []).Select((d, i) => (d, i)))
        {
            if (!BindingMapper.TryFromDto(dto, out var binding, out var error))
            {
                errors.Add($"第 {index + 1} 条绑定被忽略:{error}");
            }
            else if (!seen.Add(binding!.Id))
            {
                errors.Add($"第 {index + 1} 条绑定被忽略:id {binding.Id} 重复");
            }
            else
            {
                bindings.Add(binding);
            }
        }

        if (errors.Count > 0)
        {
            BackupWhenLossy(path);
        }

        return new BindingLoadResult(bindings, errors);
    }

    public async Task SaveAsync(IReadOnlyList<CommandBinding> bindings, CancellationToken cancellationToken = default)
    {
        var file = new BindingsFileDto { Bindings = [.. bindings.Select(BindingMapper.ToDto)] };
        await SafeJsonFile.WriteAtomicAsync(paths.BindingsFile, SafeJsonFile.Serialize(file), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>有条目被丢弃时,保留一份原文件副本(复制而非移动,原文件继续生效)。</summary>
    private void BackupWhenLossy(string path)
    {
        var backup = $"{path}.lossy-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Copy(path, backup);
            logger.LogWarning("绑定文件有条目被忽略,原文件已备份为 {Backup}", backup);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "备份绑定文件失败");
        }
    }
}
