namespace Waio.Feature.Clipboard.Domain;

internal enum PanelKey
{
    Other,
    Up,
    Down,
    Enter,
    Escape,
    Space,
    Backspace,
    Delete,
    P,
    F,
    W,

    /// <summary>主键盘或小键盘的数字键,具体数字见 <see cref="PanelKeyInput.Digit"/>。</summary>
    Digit,
}

/// <param name="Digit">仅当 <see cref="Key"/> 为 <see cref="PanelKey.Digit"/> 时有意义(0~9)。</param>
/// <param name="Repeat">按住不放产生的自动重复(不是新的一次按下)。</param>
internal readonly record struct PanelKeyInput(
    PanelKey Key, bool Ctrl = false, bool Shift = false, int Digit = -1, bool Repeat = false);

/// <param name="SearchFocused">
/// 键盘焦点在搜索框里。explicit 模式下是用户按了 Ctrl+F 或点了搜索框;typeToSearch 模式下恒为 true。
/// </param>
/// <param name="Composing">输入法正在组字(拼音候选等)——此时所有按键都归输入法。</param>
/// <param name="Preview">空格预览的方式:按一下开/再按一下关,或按住预览、松开关闭。</param>
internal readonly record struct PanelContext(
    SearchMode Mode, bool SearchFocused, bool Composing, bool PreviewOpen, bool QueryEmpty,
    PreviewMode Preview = PreviewMode.Toggle);

internal enum PanelActionKind
{
    None,
    MoveUp,
    MoveDown,
    Paste,
    PastePlain,
    QuickPaste,
    TogglePreview,

    /// <summary>打开预览(按住空格预览模式下的按下);已打开则什么都不做。</summary>
    OpenPreview,
    ClosePreview,
    Delete,
    TogglePin,
    StartSearch,
    EndSearch,

    /// <summary>从搜索框下移到列表,选中第一条(搜索框里按 ↓)。</summary>
    EnterList,
    ClearSearch,
    Close,

    /// <summary>吃掉这个键、什么都不做(例如按住空格的自动重复,不能漏给列表去触发"点击=粘贴")。</summary>
    Swallow,
}

internal readonly record struct PanelAction(PanelActionKind Kind, int Slot = -1)
{
    public static PanelAction None { get; } = new(PanelActionKind.None);
}

/// <summary>
/// 面板的键位表(纯函数,可单测;键位参考 MAIO 的剪贴板 README,⌘ 换成 Ctrl)。
/// <para>
/// 关键约束:<b>输入法组字期间一律返回 None</b>——组字时的 ⏎ 是"上屏"、空格是"选词"、⌫ 是"删拼音"、Esc 是"取消组字",
/// 绝不能被当成"粘贴""预览""删除""关闭"。这是中文用户最容易踩的坑。
/// </para>
/// </summary>
internal static class PanelKeys
{
    public static PanelAction Resolve(PanelKeyInput key, PanelContext ctx)
    {
        if (ctx.Composing)
        {
            return PanelAction.None;
        }

        return key.Ctrl ? ResolveCtrl(key, ctx) : ResolvePlain(key, ctx);
    }

    private static PanelAction ResolveCtrl(PanelKeyInput key, PanelContext ctx) => key.Key switch
    {
        PanelKey.Digit when key.Digit is >= 0 and <= 9 => new PanelAction(PanelActionKind.QuickPaste, key.Digit),
        PanelKey.P => new(PanelActionKind.TogglePin),
        PanelKey.W => new(PanelActionKind.Close),
        PanelKey.F when ctx.Mode == SearchMode.Explicit => new(PanelActionKind.StartSearch),
        // 在搜索框里有文字时 Ctrl+Backspace 是"删一个词",归文本框
        PanelKey.Backspace when !(ctx.SearchFocused && !ctx.QueryEmpty) => new(PanelActionKind.Delete),
        _ => PanelAction.None, // Ctrl+A/C/V/X/Z 等照常交给文本框
    };

    private static PanelAction ResolvePlain(PanelKeyInput key, PanelContext ctx)
    {
        var explicitMode = ctx.Mode == SearchMode.Explicit;
        var inSearchBox = explicitMode && ctx.SearchFocused;
        return key.Key switch
        {
            // 焦点在搜索框:↓ 进入列表(选中第一条),↑ 留给文本框
            PanelKey.Down when inSearchBox => new(PanelActionKind.EnterList),
            PanelKey.Up when inSearchBox => PanelAction.None,
            PanelKey.Up => new(PanelActionKind.MoveUp),
            PanelKey.Down => new(PanelActionKind.MoveDown),

            // explicit 搜索中:⏎ 只是"结束搜索"(保留结果),再按一次才粘贴
            PanelKey.Enter when explicitMode && ctx.SearchFocused => new(PanelActionKind.EndSearch),
            PanelKey.Enter => new(key.Shift ? PanelActionKind.PastePlain : PanelActionKind.Paste),

            // Esc 逐层退出:预览 → 搜索结果 → 面板
            PanelKey.Escape when ctx.PreviewOpen => new(PanelActionKind.ClosePreview),
            PanelKey.Escape when explicitMode && (ctx.SearchFocused || !ctx.QueryEmpty) => new(PanelActionKind.ClearSearch),
            PanelKey.Escape => new(PanelActionKind.Close),

            // 空格/删除:搜索框有焦点(或有文字)时是打字,否则是预览/删除条目
            PanelKey.Space when TypingIntoSearch(ctx) => PanelAction.None,
            PanelKey.Space when key.Repeat => new(PanelActionKind.Swallow), // 按住不放不能反复开关预览
            PanelKey.Space when ctx.Preview == PreviewMode.Hold => new(PanelActionKind.OpenPreview),
            PanelKey.Space => new(PanelActionKind.TogglePreview),
            PanelKey.Backspace or PanelKey.Delete when TypingIntoSearch(ctx) => PanelAction.None,
            PanelKey.Backspace or PanelKey.Delete => new(PanelActionKind.Delete),

            _ => PanelAction.None,
        };
    }

    /// <summary>松开按键:只有"按住空格预览"模式下松开空格要关闭预览。</summary>
    public static PanelAction ResolveKeyUp(PanelKeyInput key, PanelContext ctx) =>
        !ctx.Composing && key.Key == PanelKey.Space && ctx.Preview == PreviewMode.Hold && ctx.PreviewOpen
            ? new PanelAction(PanelActionKind.ClosePreview)
            : PanelAction.None;

    /// <summary>这个键此刻应该归搜索框(编辑文字)而不是列表?</summary>
    private static bool TypingIntoSearch(PanelContext ctx) =>
        ctx.Mode == SearchMode.Explicit ? ctx.SearchFocused : !ctx.QueryEmpty;
}
