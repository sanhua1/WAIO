using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>
/// 平台无关的键名("v"、"f1"、"space"、"lalt")与 Windows 虚拟键码互转。
/// 修饰键在事件里区分左右(lctrl/rctrl…);在 <see cref="KeyChord"/> 里只有 ctrl/shift/alt/win 四种,不区分左右。
/// </summary>
public static class KeyNames
{
    public const int LCtrl = 1, RCtrl = 2, LShift = 4, RShift = 8, LAlt = 16, RAlt = 32, LWin = 64, RWin = 128;

    /// <summary>用于抑制"单按 Win/Alt 松开后弹出开始菜单/菜单栏"的无意义按键(VK 0xE8 未分配)。</summary>
    public const int MaskKeyVk = 0xE8;

    private static readonly Dictionary<int, string> VkToName = [];
    private static readonly Dictionary<string, int> NameToVk = [];

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["escape"] = "esc",
        ["return"] = "enter",
        ["del"] = "delete",
        ["ins"] = "insert",
        ["pgup"] = "pageup",
        ["pgdn"] = "pagedown",
        ["spacebar"] = "space",
        ["back"] = "backspace",
        ["bksp"] = "backspace",
        ["caps"] = "capslock",
        ["prtsc"] = "printscreen",
    };

    private static readonly HashSet<int> Extended =
    [
        0xA3, 0xA5, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x90, 0x6F, 0x5B, 0x5C, 0x5D,
    ];

    static KeyNames()
    {
        for (var i = 0; i < 26; i++)
        {
            Add(0x41 + i, ((char)('a' + i)).ToString());
        }

        for (var i = 0; i < 10; i++)
        {
            Add(0x30 + i, i.ToString());
            Add(0x60 + i, "num" + i);
        }

        for (var i = 1; i <= 24; i++)
        {
            Add(0x70 + i - 1, "f" + i);
        }

        (int Vk, string Name)[] named =
        [
            (0x08, "backspace"), (0x09, "tab"), (0x0D, "enter"), (0x13, "pause"), (0x14, "capslock"), (0x1B, "esc"),
            (0x20, "space"), (0x21, "pageup"), (0x22, "pagedown"), (0x23, "end"), (0x24, "home"),
            (0x25, "left"), (0x26, "up"), (0x27, "right"), (0x28, "down"),
            (0x2C, "printscreen"), (0x2D, "insert"), (0x2E, "delete"), (0x5D, "apps"),
            (0x6A, "nummultiply"), (0x6B, "numadd"), (0x6D, "numsubtract"), (0x6E, "numdecimal"), (0x6F, "numdivide"),
            (0x90, "numlock"), (0x91, "scrolllock"),
            (0xA0, "lshift"), (0xA1, "rshift"), (0xA2, "lctrl"), (0xA3, "rctrl"), (0xA4, "lalt"), (0xA5, "ralt"),
            (0x5B, "lwin"), (0x5C, "rwin"),
            (0xBA, "semicolon"), (0xBB, "equals"), (0xBC, "comma"), (0xBD, "minus"), (0xBE, "period"),
            (0xBF, "slash"), (0xC0, "grave"), (0xDB, "lbracket"), (0xDC, "backslash"), (0xDD, "rbracket"), (0xDE, "quote"),
        ];

        foreach (var (vk, name) in named)
        {
            Add(vk, name);
        }
    }

    /// <summary>
    /// 可以做快捷键主键的所有键名(不含修饰键),按"常用程度"排列:F1–F24、字母、数字、功能键、符号、小键盘。
    /// 绑定编辑器的手动选键列表用它——没有物理 F13–F24 的键盘也能选到。
    /// </summary>
    public static IReadOnlyList<string> PrimaryKeys { get; } = BuildPrimaryKeys();

    public static bool TryGetName(int vk, out string name) => VkToName.TryGetValue(vk, out name!);

    public static bool TryGetVk(string name, out int vk) => NameToVk.TryGetValue(Canonicalize(name), out vk);

    /// <summary>把别名(escape、del、pgup…)统一成规范名;未知名字原样小写返回。</summary>
    public static string Canonicalize(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        return Aliases.GetValueOrDefault(lower, lower);
    }

    public static bool IsKnown(string name) => NameToVk.ContainsKey(Canonicalize(name));

    /// <summary>主键和自定义修饰键都换成规范名("Escape"→"esc"),让同一个组合只有一种写法。</summary>
    public static KeyChord Canonicalize(KeyChord chord) =>
        new(chord.Modifiers, Canonicalize(chord.Key), chord.Hold is null ? null : Canonicalize(chord.Hold));

    /// <summary>
    /// 能不能声明为自定义修饰键(ADR-0012):F1–F24、CapsLock、ScrollLock、Pause、Insert、Menu 等不用来打字的键。
    /// 字母、数字、符号、空格、回车不行——它们按下时会被吞掉、松开才补发,打字会变得别扭。
    /// </summary>
    public static bool CanBeCustomModifier(string name)
    {
        var key = Canonicalize(name);
        return IsKnown(key) && (key is "capslock" or "scrolllock" or "pause" or "insert" or "apps"
            || (key.Length is 2 or 3 && key[0] == 'f' && key[1..].All(char.IsDigit)));
    }

    /// <summary>可以声明为自定义修饰键的全部按键(设置页的下拉列表)。</summary>
    public static IReadOnlyList<string> CustomModifierCandidates { get; } =
        [.. Enumerable.Range(1, 24).Select(i => "f" + i).Reverse(), "capslock", "scrolllock", "pause", "insert", "apps"];

    /// <summary>修饰键的位(左右各一位);非修饰键返回 0。</summary>
    public static int ModifierBit(int vk) => vk switch
    {
        0xA2 => LCtrl,
        0xA3 => RCtrl,
        0xA0 => LShift,
        0xA1 => RShift,
        0xA4 => LAlt,
        0xA5 => RAlt,
        0x5B => LWin,
        0x5C => RWin,
        _ => 0,
    };

    public static KeyModifiers ToModifiers(int mask)
    {
        var result = KeyModifiers.None;
        if ((mask & (LCtrl | RCtrl)) != 0) { result |= KeyModifiers.Ctrl; }
        if ((mask & (LShift | RShift)) != 0) { result |= KeyModifiers.Shift; }
        if ((mask & (LAlt | RAlt)) != 0) { result |= KeyModifiers.Alt; }
        if ((mask & (LWin | RWin)) != 0) { result |= KeyModifiers.Win; }
        return result;
    }

    /// <summary>发送按键时要不要带 EXTENDEDKEY 标志(方向键、Insert/Delete、右 Ctrl/Alt、Win 键等)。</summary>
    public static bool IsExtended(int vk) => Extended.Contains(vk);

    private static List<string> BuildPrimaryKeys()
    {
        var keys = new List<string>();
        keys.AddRange(Enumerable.Range(1, 24).Select(i => "f" + i));
        keys.AddRange(Enumerable.Range(0, 26).Select(i => ((char)('a' + i)).ToString()));
        keys.AddRange(Enumerable.Range(0, 10).Select(i => i.ToString()));
        keys.AddRange(
        [
            "space", "enter", "tab", "esc", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
            "up", "down", "left", "right", "printscreen", "pause", "scrolllock", "capslock", "numlock", "apps",
            "grave", "minus", "equals", "lbracket", "rbracket", "backslash", "semicolon", "quote", "comma", "period", "slash",
        ]);
        keys.AddRange(Enumerable.Range(0, 10).Select(i => "num" + i));
        keys.AddRange(["numadd", "numsubtract", "nummultiply", "numdivide", "numdecimal"]);
        return keys;
    }

    private static void Add(int vk, string name)
    {
        VkToName[vk] = name;
        NameToVk[name] = vk;
    }
}
