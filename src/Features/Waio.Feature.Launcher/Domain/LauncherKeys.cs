namespace Waio.Feature.Launcher.Domain;

/// <summary>面板关心的按键(视图把 WinUI 的 VirtualKey 映射成这些,Domain 不引用 WinUI)。</summary>
internal enum LauncherKey
{
    Other,
    Up,
    Down,
    Enter,
    Escape,
    Tab,
    Backspace,
    Digit,
    C,
    N,
    P,
}

/// <param name="Digit">Key 为 <see cref="LauncherKey.Digit"/> 时是哪个数字。</param>
internal readonly record struct LauncherKeyInput(LauncherKey Key, bool Ctrl = false, bool Shift = false, bool Alt = false, int Digit = 0);

internal enum LauncherActionKind
{
    /// <summary>不处理,交给搜索框(打字、左右移动光标、Ctrl+A、Ctrl+V……)。</summary>
    None,
    MoveUp,
    MoveDown,
    Open,

    /// <summary>在资源管理器中显示(Ctrl+⏎)。</summary>
    Reveal,

    /// <summary>复制所选项的路径 / 网址(搜索框里没有选中文字时的 Ctrl+C)。</summary>
    Copy,
    QuickOpen,
    NextSource,
    PreviousSource,
    Escape,

    /// <summary>输入为空时的退格:回到"全部"。</summary>
    BackspaceOnEmpty,
}

internal readonly record struct LauncherAction(LauncherActionKind Kind, int Slot = 0)
{
    public static LauncherAction None => default;
}

/// <summary>按键当时的环境。</summary>
/// <param name="Composing">输入法正在组字:所有按键都交给输入法(剪贴板搜索踩过的坑)。</param>
/// <param name="HasTextSelection">搜索框里选中了文字:Ctrl+C 是普通复制。</param>
/// <param name="QueryEmpty">搜索框是空的。</param>
internal readonly record struct LauncherKeyContext(bool Composing, bool HasTextSelection, bool QueryEmpty);

/// <summary>
/// 面板里的按键表(MAIO 的 LauncherController.intercept,⌘ 换成 Ctrl):
/// ↑↓ / Ctrl+P、Ctrl+N 选择 · ⏎ 打开 · Ctrl+⏎ 打开文件位置 · Ctrl+C 复制路径 · Ctrl+1~9 打开第几项 ·
/// Tab / Shift+Tab 切换来源(← → 留给搜索框移动光标)· 输入为空时退格回到"全部" · Esc 先清空再关闭。
/// </summary>
internal static class LauncherKeys
{
    public static LauncherAction Resolve(LauncherKeyInput input, LauncherKeyContext context)
    {
        if (context.Composing || input.Alt)
        {
            return LauncherAction.None;
        }

        var plain = !input.Ctrl && !input.Shift;
        var ctrlOnly = input.Ctrl && !input.Shift;
        return input.Key switch
        {
            LauncherKey.Up when plain => new(LauncherActionKind.MoveUp),
            LauncherKey.Down when plain => new(LauncherActionKind.MoveDown),
            LauncherKey.P when ctrlOnly => new(LauncherActionKind.MoveUp),
            LauncherKey.N when ctrlOnly => new(LauncherActionKind.MoveDown),
            LauncherKey.Enter when plain => new(LauncherActionKind.Open),
            LauncherKey.Enter when ctrlOnly => new(LauncherActionKind.Reveal),
            LauncherKey.Escape when plain => new(LauncherActionKind.Escape),
            LauncherKey.Tab when !input.Ctrl => new(input.Shift ? LauncherActionKind.PreviousSource : LauncherActionKind.NextSource),
            LauncherKey.Backspace when plain && context.QueryEmpty => new(LauncherActionKind.BackspaceOnEmpty),
            LauncherKey.C when ctrlOnly && !context.HasTextSelection => new(LauncherActionKind.Copy),
            LauncherKey.Digit when ctrlOnly && input.Digit is >= 1 and <= 9 => new(LauncherActionKind.QuickOpen, input.Digit),
            _ => LauncherAction.None,
        };
    }
}
