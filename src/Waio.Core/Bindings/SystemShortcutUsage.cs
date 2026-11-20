using Waio.Core.Input;
using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

/// <summary>一个系统快捷键现在归谁(ADR-0029)。</summary>
public enum SystemShortcutState
{
    /// <summary>没被 WAIO 占用:系统功能照常。</summary>
    System,

    /// <summary>被一条全局生效的 WAIO 快捷键占用:WAIO 运行时系统功能停用。</summary>
    TakenOver,

    /// <summary>只在某些程序里被占用(绑定有生效范围):其余程序里系统功能照常。</summary>
    PartlyTakenOver,

    /// <summary>WAIO 绑了它,但它是系统保留键、拦不住:按下时系统功能和 WAIO 命令都会触发。</summary>
    Clash,
}

/// <param name="Bindings">占用它的 WAIO 绑定(只含真正生效的:启用、命令可用、组合一致)。</param>
public sealed record SystemShortcutUsage(SystemShortcut Shortcut, SystemShortcutState State, IReadOnlyList<CommandBinding> Bindings);

/// <summary>
/// 计算系统快捷键的占用状态,以及"恢复系统功能"(ADR-0029)。纯逻辑,界面只展示结论。
/// 接管靠输入引擎的钩子吞掉按键,不改注册表:绑定不生效了(停用 / 删除 / 功能关闭 / WAIO 退出),系统功能自动回来。
/// </summary>
public static class SystemShortcutUsages
{
    /// <summary>全部系统快捷键的状态,顺序同 <see cref="KnownSystemShortcuts.All"/>。</summary>
    public static IReadOnlyList<SystemShortcutUsage> Evaluate(IEnumerable<CommandBinding> bindings, Func<CommandId, bool> isAvailable)
    {
        var active = bindings.Where(b => IsActive(b, isAvailable)).ToList();
        return [.. KnownSystemShortcuts.All.Select(s => Of(s, active.Where(b => Occupies(b, s.Chord)).ToList()))];
    }

    /// <summary>一条绑定当前占用的系统快捷键(没有占用返回 null)。</summary>
    public static SystemShortcutUsage? For(CommandBinding binding, Func<CommandId, bool> isAvailable)
    {
        if (!IsActive(binding, isAvailable) || binding.Trigger is not HotkeyTrigger { Chord: var chord }
            || KnownSystemShortcuts.Find(chord) is not { } shortcut)
        {
            return null;
        }

        return Of(shortcut, [binding]);
    }

    /// <summary>
    /// 恢复系统功能:把所有占用这个组合的启用中的绑定改成停用(不删除,用户随时可以在快捷键页重新打开)。
    /// 命令暂时不可用的也一起停用,否则功能一打开又会占回去。
    /// </summary>
    public static List<CommandBinding> Release(IEnumerable<CommandBinding> bindings, KeyChord chord)
    {
        var target = KeyNames.Canonicalize(chord);
        return [.. bindings.Select(b => b.Enabled && Occupies(b, target) ? b with { Enabled = false } : b)];
    }

    private static SystemShortcutUsage Of(SystemShortcut shortcut, IReadOnlyList<CommandBinding> occupiers)
    {
        var state = occupiers.Count == 0 ? SystemShortcutState.System
            : !shortcut.CanOverride ? SystemShortcutState.Clash
            : occupiers.Any(b => b.Scope is not ProcessScope) ? SystemShortcutState.TakenOver
            : SystemShortcutState.PartlyTakenOver;
        return new SystemShortcutUsage(shortcut, state, occupiers);
    }

    private static bool IsActive(CommandBinding binding, Func<CommandId, bool> isAvailable) =>
        binding.Enabled && isAvailable(binding.Command);

    private static bool Occupies(CommandBinding binding, KeyChord chord) =>
        binding.Trigger is HotkeyTrigger { Chord: var c } && c.Hold is null && KeyNames.Canonicalize(c) == chord;
}
