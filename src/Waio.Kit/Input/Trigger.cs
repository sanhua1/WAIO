using Waio.Kit.Commands;

namespace Waio.Kit.Input;

/// <summary>
/// 触发器:产生"执行某命令"的请求。今天只有快捷键,以后加双击修饰键、长按、鼠标侧键、手势,
/// 都是新增一个子类型 + 一个 Matcher 状态机,不改任何功能代码。
/// </summary>
public abstract record Trigger;

public sealed record HotkeyTrigger(KeyChord Chord) : Trigger;

/// <summary>绑定的生效范围。</summary>
public abstract record BindingScope
{
    public static BindingScope Global { get; } = new GlobalScope();
}

public sealed record GlobalScope : BindingScope;

/// <summary>只在(或除了)某些进程里生效,按进程名(不含 .exe,不区分大小写)匹配。</summary>
public sealed record ProcessScope(IReadOnlyList<string> ProcessNames, bool Exclude) : BindingScope;

/// <summary>用户数据:把触发器连到命令上。"任意组合键 → 任意动作"就是这个模型的自然结果。</summary>
public sealed record CommandBinding(
    Guid Id,
    Trigger Trigger,
    CommandId Command,
    CommandArguments Arguments,
    BindingScope Scope,
    bool Enabled = true);
