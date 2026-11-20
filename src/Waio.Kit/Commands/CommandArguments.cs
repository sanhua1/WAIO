using System.Text.Json;

namespace Waio.Kit.Commands;

/// <summary>无返回值的占位类型,用于"没有输入/输出"的契约命令。</summary>
public readonly record struct Unit;

/// <summary>
/// 命令参数:来自用户绑定(bindings.json)的键值对。值保持 JsonElement,
/// 由命令 handler 自己按需解释,宿主不关心类型。
/// </summary>
public sealed record CommandArguments(IReadOnlyDictionary<string, JsonElement> Values)
{
    public static CommandArguments Empty { get; } = new(new Dictionary<string, JsonElement>());

    public string? GetString(string name) =>
        Values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public bool? GetBool(string name) =>
        Values.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
