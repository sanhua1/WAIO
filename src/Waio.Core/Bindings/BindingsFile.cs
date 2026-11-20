using System.Text.Json;
using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

/// <summary>bindings.json 的磁盘形状(DTO)。领域类型是多态记录,不直接序列化,避免磁盘格式被类型重构绑架。</summary>
internal sealed class BindingsFileDto
{
    public int SchemaVersion { get; set; } = 1;

    public List<BindingDto> Bindings { get; set; } = [];
}

internal sealed class BindingDto
{
    public Guid? Id { get; set; }

    public TriggerDto? Trigger { get; set; }

    public string? Command { get; set; }

    public Dictionary<string, JsonElement>? Arguments { get; set; }

    public ScopeDto? Scope { get; set; }

    public bool Enabled { get; set; } = true;
}

internal sealed class TriggerDto
{
    public string? Hotkey { get; set; }
}

internal sealed class ScopeDto
{
    public string? Type { get; set; }

    public bool Exclude { get; set; }

    public List<string>? Names { get; set; }
}

/// <summary>DTO 与领域类型互转。解析是严格的:任何字段不合法,整条绑定被拒绝并给出原因。</summary>
internal static class BindingMapper
{
    public static bool TryFromDto(BindingDto dto, out CommandBinding? binding, out string error)
    {
        binding = null;

        if (dto.Id is null || dto.Id == Guid.Empty)
        {
            error = "缺少 id";
            return false;
        }

        if (dto.Trigger?.Hotkey is null || !KeyChord.TryParse(dto.Trigger.Hotkey, out var chord))
        {
            error = $"触发器无效(目前只支持 hotkey):'{dto.Trigger?.Hotkey}'";
            return false;
        }

        if (!KeyNames.IsKnown(chord.Key))
        {
            error = $"未知的按键名 '{chord.Key}'(拼写错误?)";
            return false;
        }

        // 自定义修饰键(ADR-0012):必须是允许的键;有没有被用户声明不在这里判断(声明在 app.json,可能稍后才加)
        if (chord.Hold is { } hold && !KeyNames.CanBeCustomModifier(hold))
        {
            error = $"'{hold}' 不能做自定义修饰键(只支持 F1–F24、CapsLock 等不用来打字的键)";
            return false;
        }

        chord = KeyNames.Canonicalize(chord); // 别名统一成规范名

        if (string.IsNullOrWhiteSpace(dto.Command) || !TryParseCommand(dto.Command, out var command))
        {
            error = $"命令 id 无效:'{dto.Command}'";
            return false;
        }

        if (!TryParseScope(dto.Scope, out var scope, out var scopeError))
        {
            error = scopeError;
            return false;
        }

        var arguments = dto.Arguments is null
            ? CommandArguments.Empty
            : new CommandArguments(dto.Arguments.ToDictionary(p => p.Key, p => p.Value.Clone()));

        binding = new CommandBinding(dto.Id.Value, new HotkeyTrigger(chord), command, arguments, scope!, dto.Enabled);
        error = string.Empty;
        return true;
    }

    public static BindingDto ToDto(CommandBinding binding) => new()
    {
        Id = binding.Id,
        Trigger = new TriggerDto { Hotkey = ((HotkeyTrigger)binding.Trigger).Chord.ToString() },
        Command = binding.Command.ToString(),
        Arguments = binding.Arguments.Values.Count == 0
            ? null
            : binding.Arguments.Values.ToDictionary(p => p.Key, p => p.Value),
        Scope = binding.Scope switch
        {
            ProcessScope p => new ScopeDto { Type = "process", Exclude = p.Exclude, Names = [.. p.ProcessNames] },
            _ => new ScopeDto { Type = "global" },
        },
        Enabled = binding.Enabled,
    };

    private static bool TryParseCommand(string text, out CommandId command)
    {
        try
        {
            command = new CommandId(text);
            return true;
        }
        catch (ArgumentException)
        {
            command = default;
            return false;
        }
    }

    private static bool TryParseScope(ScopeDto? dto, out BindingScope? scope, out string error)
    {
        error = string.Empty;
        switch (dto?.Type)
        {
            case null:
            case "global":
                scope = BindingScope.Global;
                return true;

            case "process":
                if (dto.Names is not { Count: > 0 } || dto.Names.Any(string.IsNullOrWhiteSpace))
                {
                    scope = null;
                    error = "process 作用域必须提供非空的进程名列表";
                    return false;
                }

                scope = new ProcessScope([.. dto.Names.Select(n => n.Trim())], dto.Exclude);
                return true;

            default:
                scope = null;
                error = $"未知的作用域类型 '{dto.Type}'";
                return false;
        }
    }
}
