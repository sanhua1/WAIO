using System.Text.Json;
using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

public enum BindingIssueLevel
{
    /// <summary>提示:可以保存,但用户应该知道(覆盖系统快捷键、和其他绑定冲突)。</summary>
    Warning,

    /// <summary>错误:不能保存(没有快捷键、命令不存在、必填参数为空)。</summary>
    Error,
}

public sealed record BindingIssue(BindingIssueLevel Level, string Message);

/// <summary>
/// 图形化编辑绑定用的纯逻辑(ADR-0011):增删改列表、保存前检查。界面只负责展示这里给出的结论。
/// </summary>
public static class BindingRules
{
    /// <summary>新建绑定用的 id。</summary>
    public static Guid NewId() => Guid.NewGuid();

    /// <summary>按 id 替换;没有就追加到末尾(保持其余绑定的顺序——冲突时顺序决定谁生效)。</summary>
    public static List<CommandBinding> Upsert(IEnumerable<CommandBinding> bindings, CommandBinding binding)
    {
        var list = bindings.ToList();
        var index = list.FindIndex(b => b.Id == binding.Id);
        if (index >= 0)
        {
            list[index] = binding;
        }
        else
        {
            list.Add(binding);
        }

        return list;
    }

    public static List<CommandBinding> Remove(IEnumerable<CommandBinding> bindings, Guid id) =>
        [.. bindings.Where(b => b.Id != id)];

    /// <summary>
    /// 保存前检查一条绑定(与其余绑定 <paramref name="others"/> 对照)。
    /// </summary>
    /// <param name="customModifiers">用户声明的自定义修饰键(ADR-0012);组合里用到未声明的键是错误——那样它永远不会触发。</param>
    public static IReadOnlyList<BindingIssue> Check(
        CommandBinding binding,
        IEnumerable<CommandBinding> others,
        IReadOnlyList<FeatureManifest> manifests,
        IReadOnlyCollection<string>? customModifiers = null)
    {
        var issues = new List<BindingIssue>();
        if (binding.Trigger is not HotkeyTrigger { Chord: var chord } || string.IsNullOrEmpty(chord.Key))
        {
            issues.Add(new BindingIssue(BindingIssueLevel.Error, "还没有设置快捷键"));
            return issues;
        }

        if (!KeyNames.IsKnown(chord.Key))
        {
            issues.Add(new BindingIssue(BindingIssueLevel.Error, $"不认识的按键 '{chord.Key}'"));
        }
        else if (chord.Hold is { } hold)
        {
            if (!KeyNames.CanBeCustomModifier(hold))
            {
                issues.Add(new BindingIssue(BindingIssueLevel.Error, $"'{hold}' 不能做自定义修饰键"));
            }
            else if (!(customModifiers ?? []).Contains(KeyNames.Canonicalize(hold)))
            {
                issues.Add(new BindingIssue(
                    BindingIssueLevel.Error, $"{hold.ToUpperInvariant()} 还不是自定义修饰键,先在\"WAIO 快捷键\"页顶部把它加为自定义修饰键"));
            }
            else if (KeyNames.Canonicalize(hold) == KeyNames.Canonicalize(chord.Key))
            {
                issues.Add(new BindingIssue(BindingIssueLevel.Error, "自定义修饰键和主键不能是同一个键"));
            }
        }
        else if (chord.Modifiers == KeyModifiers.None && !ChordRecorder.IsStandalone(chord.Key)
            && !(customModifiers ?? []).Contains(KeyNames.Canonicalize(chord.Key)))
        {
            issues.Add(new BindingIssue(BindingIssueLevel.Error, "这个键不能单独做快捷键(会影响打字),请加上修饰键"));
        }

        var owner = manifests.FirstOrDefault(m => m.Commands.Any(c => c.Id == binding.Command));
        var descriptor = owner?.Commands.First(c => c.Id == binding.Command);
        if (descriptor is null)
        {
            issues.Add(new BindingIssue(BindingIssueLevel.Error, $"命令 {binding.Command} 不存在"));
        }
        else
        {
            foreach (var spec in descriptor.Parameters.Where(p => p.Required && IsBlank(binding.Arguments, p.Name)))
            {
                issues.Add(new BindingIssue(BindingIssueLevel.Error, $"\"{spec.Title}\"不能为空"));
            }
        }

        if (KnownSystemShortcuts.Find(chord) is { } system)
        {
            issues.Add(system.CanOverride
                ? new BindingIssue(BindingIssueLevel.Warning,
                    $"{chord} 与系统快捷键「{system.Function}」相同:保存后,WAIO 运行时按下只会触发 WAIO,系统的功能不会触发;停用、删除这条快捷键或退出 WAIO 后,系统快捷键恢复工作")
                : new BindingIssue(BindingIssueLevel.Error,
                    $"{chord} 是系统保留快捷键「{system.Function}」,WAIO 接管不了({system.Note}),请换一个组合"));
        }

        if (binding.Enabled)
        {
            foreach (var other in others.Where(o => o.Id != binding.Id && o.Enabled && SameChord(o, chord)))
            {
                if (BindingConflictDetector.ScopesOverlap(binding.Scope, other.Scope))
                {
                    issues.Add(new BindingIssue(
                        BindingIssueLevel.Warning, $"和已有的快捷键冲突:{Describe(other.Command, manifests)}(先保存的那条生效)"));
                }
            }
        }

        return issues;
    }

    /// <summary>只保留命令声明过的参数,去掉空字符串(切换命令后旧参数不应残留在文件里)。</summary>
    public static CommandArguments CleanArguments(CommandArguments arguments, CommandDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return arguments;
        }

        var allowed = descriptor.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var kept = arguments.Values
            .Where(p => allowed.Contains(p.Key) && !(p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim().Length == 0))
            .ToDictionary(p => p.Key, p => p.Value);
        return kept.Count == 0 ? CommandArguments.Empty : new CommandArguments(kept);
    }

    public static string Describe(CommandId command, IReadOnlyList<FeatureManifest> manifests) =>
        manifests.SelectMany(m => m.Commands).FirstOrDefault(c => c.Id == command)?.Title ?? command.Value;

    private static bool IsBlank(CommandArguments arguments, string name) =>
        !arguments.Values.TryGetValue(name, out var value)
        || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static bool SameChord(CommandBinding binding, KeyChord chord) =>
        binding.Trigger is HotkeyTrigger { Chord: var other } && Normalize(other) == Normalize(chord);

    private static KeyChord Normalize(KeyChord chord) => KeyNames.Canonicalize(chord);
}
