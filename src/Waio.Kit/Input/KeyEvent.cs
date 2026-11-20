namespace Waio.Kit.Input;

public enum KeyEventKind
{
    Down,
    Up,
}

/// <summary>规范化后的按键事件(已把虚拟键码映射成平台无关的 Key 名)。</summary>
/// <param name="Key">"v"、"f1"、"tab"、"lalt"(左 Alt)等。</param>
/// <param name="Modifiers">事件发生时的修饰键状态。</param>
/// <param name="IsRepeat">长按产生的自动重复。</param>
public readonly record struct KeyEvent(KeyEventKind Kind, string Key, KeyModifiers Modifiers, bool IsRepeat);

/// <summary>过滤器对事件的处置。</summary>
public enum FilterDecision
{
    /// <summary>放行,继续交给后面的过滤器和目标程序。</summary>
    Pass,

    /// <summary>吞掉,目标程序收不到。</summary>
    Consume,
}
