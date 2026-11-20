namespace Waio.Kit.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,

    /// <summary>Windows 键。</summary>
    Win = 1 << 3,
}

/// <summary>
/// 快捷键组合:修饰键 + (可选的)一个自定义修饰键 + 一个主键。键用平台无关的名字("v"、"f1"、"space"、"f24"),
/// 由 Core 的输入引擎映射成虚拟键码,Kit 因此保持平台无关。
/// <para>
/// 文本形式如 "ctrl+shift+v";带自定义修饰键时如 "ctrl+f24+j"(F24 按住期间按 J)。小写,
/// 顺序固定为 ctrl, shift, alt, win, 自定义修饰键, 主键,便于 bindings.json 手写和 diff。
/// 哪些键可以做自定义修饰键由用户声明(ADR-0012),Kit 只负责表示,不做校验。
/// </para>
/// </summary>
/// <param name="Hold">自定义修饰键(按住它再按主键);没有为 null。</param>
public readonly record struct KeyChord(KeyModifiers Modifiers, string Key, string? Hold = null)
{
    public override string ToString()
    {
        var parts = new List<string>(6);
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) { parts.Add("ctrl"); }
        if (Modifiers.HasFlag(KeyModifiers.Shift)) { parts.Add("shift"); }
        if (Modifiers.HasFlag(KeyModifiers.Alt)) { parts.Add("alt"); }
        if (Modifiers.HasFlag(KeyModifiers.Win)) { parts.Add("win"); }
        if (Hold is not null) { parts.Add(Hold); }
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static KeyChord Parse(string text)
    {
        if (!TryParse(text, out var chord))
        {
            throw new FormatException($"无法解析快捷键 '{text}',期望形如 'ctrl+shift+v' 或 'f24+j'");
        }

        return chord;
    }

    /// <summary>
    /// 解析快捷键文本。除标准修饰键外最多两个键:只有一个时它是主键;两个时前一个是自定义修饰键、后一个是主键。
    /// </summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        var keys = new List<string>(2);
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = raw.ToLowerInvariant();
            var modifier = part switch
            {
                "ctrl" or "control" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "win" or "meta" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };

            if (modifier != KeyModifiers.None)
            {
                modifiers |= modifier;
            }
            else if (part.Length > 0 && keys.Count < 2)
            {
                keys.Add(part);
            }
            else
            {
                return false; // 空片段,或超过两个非修饰键
            }
        }

        switch (keys.Count)
        {
            case 1:
                chord = new KeyChord(modifiers, keys[0]);
                return true;
            case 2 when keys[0] != keys[1]:
                chord = new KeyChord(modifiers, keys[1], keys[0]);
                return true;
            default:
                return false;
        }
    }
}
