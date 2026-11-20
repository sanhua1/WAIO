using Waio.Kit.Input;

namespace Waio.UI;

/// <summary>
/// 快捷键的显示文字(键帽上印的样子):"ctrl+shift+pageup" → ["Ctrl", "Shift", "PgUp"]。
/// 只管显示;存储格式仍是 <see cref="KeyChord.ToString"/> 的小写形式。
/// </summary>
public static class KeyChordText
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["esc"] = "Esc", ["enter"] = "Enter", ["tab"] = "Tab", ["space"] = "Space", ["backspace"] = "Backspace",
        ["delete"] = "Del", ["insert"] = "Ins", ["home"] = "Home", ["end"] = "End", ["pageup"] = "PgUp", ["pagedown"] = "PgDn",
        ["up"] = "↑", ["down"] = "↓", ["left"] = "←", ["right"] = "→",
        ["printscreen"] = "PrtSc", ["pause"] = "Pause", ["scrolllock"] = "ScrLk", ["capslock"] = "Caps", ["numlock"] = "NumLk",
        ["apps"] = "Menu",
        ["grave"] = "`", ["minus"] = "-", ["equals"] = "=", ["lbracket"] = "[", ["rbracket"] = "]", ["backslash"] = "\\",
        ["semicolon"] = ";", ["quote"] = "'", ["comma"] = ",", ["period"] = ".", ["slash"] = "/",
        ["numadd"] = "Num +", ["numsubtract"] = "Num -", ["nummultiply"] = "Num *", ["numdivide"] = "Num /", ["numdecimal"] = "Num .",
    };

    /// <summary>每个键一段,用来画成一排键帽。</summary>
    public static IReadOnlyList<string> Parts(KeyChord chord)
    {
        var parts = new List<string>(5);
        if (chord.Modifiers.HasFlag(KeyModifiers.Ctrl)) { parts.Add("Ctrl"); }
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) { parts.Add("Shift"); }
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) { parts.Add("Alt"); }
        if (chord.Modifiers.HasFlag(KeyModifiers.Win)) { parts.Add("Win"); }
        if (chord.Hold is { } hold) { parts.Add(Key(hold)); }
        parts.Add(Key(chord.Key));
        return parts;
    }

    /// <summary>一行文字:"Ctrl + Shift + V"。</summary>
    public static string Display(KeyChord chord) => string.Join(" + ", Parts(chord));

    /// <summary>单个键名的显示文字:"f24" → "F24","num3" → "Num 3","pageup" → "PgUp"。</summary>
    public static string Key(string key)
    {
        var lower = key.ToLowerInvariant();
        if (Labels.TryGetValue(lower, out var label))
        {
            return label;
        }

        if (lower.StartsWith("num", StringComparison.Ordinal) && lower.Length == 4 && char.IsDigit(lower[3]))
        {
            return "Num " + lower[3];
        }

        return lower.Length == 1 || (lower[0] == 'f' && lower.Length <= 3 && lower[1..].All(char.IsDigit))
            ? lower.ToUpperInvariant()
            : char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}
