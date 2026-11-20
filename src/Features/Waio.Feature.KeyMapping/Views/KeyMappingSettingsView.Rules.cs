using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Feature.KeyMapping.Domain;
using Waio.UI;

namespace Waio.Feature.KeyMapping.Views;

/// <summary>
/// 设置页的"改键与双功能键"部分:每条规则一张卡片(改类型、改目标、删除),底部"添加规则"。
/// 问题(重复的键、改成自己、和 Super Caps 冲突)用 <see cref="RuleCompiler"/> 现场检查——与运行时同一套规则,
/// 有问题的那条照样保存,卡片上标出原因(运行时它会被忽略)。
/// </summary>
internal sealed partial class KeyMappingSettingsView
{
    /// <summary>规则里能选的键:常被改的键在前(CapsLock、右侧修饰键……),然后字母数字、F 键、符号。</summary>
    private static readonly string[] RuleKeys =
    [
        "capslock", "rctrl", "ralt", "rshift", "rwin", "lctrl", "lalt", "lshift", "lwin", "apps",
        "esc", "tab", "space", "enter", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
        "up", "down", "left", "right", "printscreen", "scrolllock", "pause", "numlock",
        .. Enumerable.Range(1, 24).Select(i => "f" + i),
        .. Enumerable.Range(0, 26).Select(i => ((char)('a' + i)).ToString()),
        .. Enumerable.Range(0, 10).Select(i => i.ToString()),
        "grave", "minus", "equals", "lbracket", "rbracket", "backslash", "semicolon", "quote", "comma", "period", "slash",
    ];

    private enum RuleKind
    {
        Remap,
        TapHold,
    }

    private static readonly (RuleKind, string)[] Kinds = [(RuleKind.Remap, "改成"), (RuleKind.TapHold, "轻点 / 按住")];

    private IEnumerable<UIElement> RuleSection()
    {
        var rules = _value.Rules ??= [];
        rules.RemoveAll(r => r is null);
        var issues = RuleIssues();

        yield return SettingsUi.PageIntro(
            "", // Switch
            "改键与双功能键",
            "把一个键换成另一个键,或者让它轻点和按住时各是一个键。例如右 Ctrl 轻点 = 菜单键、按住 = Ctrl。");
        yield return SettingsUi.Section("规则");

        for (var i = 0; i < rules.Count; i++)
        {
            yield return RuleCard(rules[i], issues.GetValueOrDefault(i));
        }

        yield return AddRuleCard();
        yield return SettingsUi.Notes(
            "对所有键盘生效",
            "改成:按下左边的键,发出右边的键;长按、组合键里也一样",
            "轻点 / 按住:单独轻点发出一个键,按住并配合别的键时当作另一个键",
            "有问题的规则(重复的键、改成自己等)照样保存,但不生效,卡片上会写明原因");
    }

    /// <summary>
    /// 规则序号(从 0 开始)→ 为什么不生效。键名来自下拉框(已是规范名),所以这里只做大小写规范化;
    /// 手写的别名(如 "caps")在设置页里不会被认出,运行时由输入引擎规范化后照常检查。
    /// </summary>
    private IReadOnlyDictionary<int, string> RuleIssues() =>
        RuleCompiler.Compile(_value, key => key.Trim().ToLowerInvariant()).RuleErrors;

    private Border RuleCard(KeyRuleSettings rule, string? issue)
    {
        var kind = rule.To is null && (rule.Tap is not null || rule.Hold is not null) ? RuleKind.TapHold : RuleKind.Remap;
        var kindBox = SettingsUi.Choice(Kinds, kind, k =>
        {
            if (k == RuleKind.TapHold)
            {
                rule.Tap ??= rule.To ?? "esc";
                rule.Hold ??= "lctrl";
                rule.To = null;
            }
            else
            {
                rule.To ??= rule.Tap ?? "esc";
                rule.Tap = rule.Hold = null;
            }

            SaveAndRebuild();
        });
        kindBox.MinWidth = 120;

        var controls = new List<UIElement> { kindBox };
        if (kind == RuleKind.TapHold)
        {
            controls.Add(Caption("轻点"));
            controls.Add(KeyBox(rule.Tap, x => rule.Tap = x));
            controls.Add(Caption("按住"));
            controls.Add(KeyBox(rule.Hold, x => rule.Hold = x));
        }
        else
        {
            controls.Add(KeyBox(rule.To, x => rule.To = x));
        }

        controls.Add(RemoveButton(() =>
        {
            _value.Rules.Remove(rule);
            SaveAndRebuild();
        }));

        var description = issue is null ? Describe(rule, kind) : "⚠ 不生效:" + issue;
        return SettingsUi.Card(Label(rule.From ?? "(未设置)"), description, Row([.. controls]));
    }

    private Border AddRuleCard()
    {
        var used = _value.Rules.Select(r => r.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var superCapsOn = Caps.Enabled;
        var free = RuleKeys.Where(k => !used.Contains(k) && !(superCapsOn && k == RuleCompiler.SuperCapsKey)).ToArray();
        var from = free.FirstOrDefault() ?? RuleKeys[1];
        var kind = RuleKind.Remap;

        var fromBox = SettingsUi.Choice(Options(free.Length > 0 ? free : RuleKeys, null), from, x => from = x);
        var kindBox = SettingsUi.Choice(Kinds, kind, k => kind = k);
        kindBox.MinWidth = 120;
        var add = SettingsUi.Button("添加", () =>
        {
            _value.Rules.Add(kind == RuleKind.Remap
                ? new KeyRuleSettings { From = from, To = "esc" }
                : new KeyRuleSettings { From = from, Tap = "esc", Hold = "lctrl" });
            SaveAndRebuild();
        });

        var hint = superCapsOn ? "选好要改的键和类型,添加后再选目标键。CapsLock 正用作 Super Caps,不在列表里" : "选好要改的键和类型,添加后再选目标键";
        return SettingsUi.Card("添加规则", hint, Row(fromBox, kindBox, add));
    }

    private ComboBox KeyBox(string? current, Action<string> changed)
    {
        var box = SettingsUi.Choice(Options(RuleKeys, current), current ?? "", x =>
        {
            changed(x);
            SaveAndRebuild(); // 说明文字和问题提示要跟着变
        });
        box.MinWidth = 130;
        return box;
    }

    private static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static string Describe(KeyRuleSettings rule, RuleKind kind) => kind == RuleKind.TapHold
        ? $"轻点 = {Label(rule.Tap ?? "?")},按住配合别的键 = {Label(rule.Hold ?? "?")}"
        : $"按下时发出 {Label(rule.To ?? "?")}";
}
