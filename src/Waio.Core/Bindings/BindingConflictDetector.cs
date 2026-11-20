using Waio.Kit.Input;

namespace Waio.Core.Bindings;

public sealed record BindingConflict(CommandBinding First, CommandBinding Second);

/// <summary>
/// 绑定冲突检测:同一快捷键在"可能同时生效"的两个作用域里被两条启用的绑定占用。
/// 保存绑定时调用,UI 上高亮提示(ARCHITECTURE §5.2 第 8 条)。
/// </summary>
public static class BindingConflictDetector
{
    public static IReadOnlyList<BindingConflict> Detect(IEnumerable<CommandBinding> bindings)
    {
        var conflicts = new List<BindingConflict>();
        var groups = bindings
            .Where(b => b is { Enabled: true, Trigger: HotkeyTrigger })
            .GroupBy(b => ((HotkeyTrigger)b.Trigger).Chord);

        foreach (var group in groups)
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (ScopesOverlap(list[i].Scope, list[j].Scope))
                    {
                        conflicts.Add(new BindingConflict(list[i], list[j]));
                    }
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// 两个作用域是否存在"某个进程同时落在两者之内"的可能。进程名不区分大小写。
    /// 全局与任何作用域重叠;"只在 I 中"与"除 E 外"重叠当且仅当 I∖E 非空;两个"除外"必然重叠(总存在第三个进程)。
    /// </summary>
    public static bool ScopesOverlap(BindingScope a, BindingScope b)
    {
        if (a is not ProcessScope pa || b is not ProcessScope pb)
        {
            return true;
        }

        var setA = ToSet(pa);
        var setB = ToSet(pb);

        return (pa.Exclude, pb.Exclude) switch
        {
            (false, false) => setA.Overlaps(setB),
            (false, true) => setA.Except(setB).Any(),
            (true, false) => setB.Except(setA).Any(),
            _ => true,
        };
    }

    private static HashSet<string> ToSet(ProcessScope scope) =>
        new(scope.ProcessNames, StringComparer.OrdinalIgnoreCase);
}
