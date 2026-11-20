using Waio.Kit.Input;

namespace Waio.Core.Input;

public enum ChordRecordOutcome
{
    /// <summary>录到一个快捷键。</summary>
    Recorded,

    /// <summary>用户按了 Backspace/Delete:清除这个快捷键。</summary>
    Cleared,

    /// <summary>用户按了 Esc,或录制被取消/超时。</summary>
    Cancelled,

    /// <summary>输入引擎没有运行(如 <c>--no-hook</c> 启动),无法录制。</summary>
    Unavailable,
}

public readonly record struct ChordRecordResult(ChordRecordOutcome Outcome, KeyChord Chord = default);

/// <summary>
/// 热键录制器的判定规则(纯逻辑,ADR-0011/0012):依次喂入一次录制中的键盘事件,决定"录到了 / 清除 / 取消 / 继续等"。
/// <list type="bullet">
/// <item>只看非修饰键的首次按下;修饰键、抬起、长按重复都只是"还在组合"。</item>
/// <item>不带修饰键的 Esc = 取消,Backspace/Delete = 清除(和 MAIO 的录制器一致)。</item>
/// <item>不带修饰键的普通字母、数字、符号键不接受——单按 A 就触发命令会让人没法打字;
/// F1–F24 等"不用来打字"的键可以单独使用(F13–F24 正适合做独立热键)。</item>
/// <item>自定义修饰键(如 F24):按下时先等;按住期间按了别的键 → 录成 "f24+j";什么都没按就松开 → 录成单独的 "f24"。</item>
/// </list>
/// 每次录制用一个新实例。
/// </summary>
public sealed class ChordRecorder(IReadOnlySet<string> customModifiers)
{
    private static readonly HashSet<string> StandaloneKeys = BuildStandaloneKeys();

    private string? _held;

    public ChordRecorder()
        : this(new HashSet<string>())
    {
    }

    /// <summary>一步的结果;<see cref="Hint"/> 是需要给用户的提示(例如"单个字母键需要配合修饰键")。</summary>
    public readonly record struct Step(ChordRecordResult? Result, string? Hint = null);

    public Step Feed(in KeyEvent keyEvent)
    {
        var key = KeyNames.Canonicalize(keyEvent.Key);
        if (customModifiers.Contains(key) && (_held is null || _held == key))
        {
            return FeedCustomModifier(keyEvent, key);
        }

        if (keyEvent.Kind != KeyEventKind.Down || keyEvent.IsRepeat || IsModifier(key))
        {
            return default;
        }

        if (_held is not null)
        {
            return new Step(new ChordRecordResult(ChordRecordOutcome.Recorded, new KeyChord(keyEvent.Modifiers, key, _held)));
        }

        if (keyEvent.Modifiers == KeyModifiers.None)
        {
            switch (key)
            {
                case "esc":
                    return new Step(new ChordRecordResult(ChordRecordOutcome.Cancelled));
                case "backspace" or "delete":
                    return new Step(new ChordRecordResult(ChordRecordOutcome.Cleared));
            }

            if (!IsStandalone(key))
            {
                return new Step(null, $"单独按 {key.ToUpperInvariant()} 会影响打字,请加上修饰键(Ctrl / Alt / Shift / Win)");
            }
        }

        return new Step(new ChordRecordResult(ChordRecordOutcome.Recorded, new KeyChord(keyEvent.Modifiers, key)));
    }

    /// <summary>这个键能不能不带修饰键单独做快捷键。</summary>
    public static bool IsStandalone(string key) => StandaloneKeys.Contains(KeyNames.Canonicalize(key));

    private Step FeedCustomModifier(in KeyEvent keyEvent, string key)
    {
        if (keyEvent.Kind == KeyEventKind.Down)
        {
            _held = key; // 按下(含长按重复):等它和别的键组合
            return default;
        }

        _held = null; // 什么都没按就松开了:录成单独的这个键
        return new Step(new ChordRecordResult(ChordRecordOutcome.Recorded, new KeyChord(keyEvent.Modifiers, key)));
    }

    private static bool IsModifier(string key) => KeyNames.TryGetVk(key, out var vk) && KeyNames.ModifierBit(vk) != 0;

    private static HashSet<string> BuildStandaloneKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "printscreen", "pause", "scrolllock", "insert", "apps" };
        for (var i = 1; i <= 24; i++)
        {
            set.Add("f" + i);
        }

        return set;
    }
}
