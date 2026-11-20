using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.UI;

namespace Waio.Feature.KeyMapping.Views;

/// <summary>
/// 按键映射的设置页(ADR-0010、ADR-0013):Super Caps 的开关和层里的按键(可改目标、删除、添加、恢复默认)。
/// 以及改键/双功能键规则(<c>KeyMappingSettingsView.Rules.cs</c>)。每改一项立即保存,运行中的功能实时生效。
/// </summary>
internal sealed partial class KeyMappingSettingsView
{
    /// <summary>可以放进层里的键(源键)。修饰键和 CapsLock 本身不行(由 RuleCompiler 强制)。</summary>
    private static readonly string[] SourceKeys =
    [
        .. Enumerable.Range(0, 26).Select(i => ((char)('a' + i)).ToString()),
        .. Enumerable.Range(0, 10).Select(i => i.ToString()),
        "space", "tab", "enter", "backspace",
        "grave", "minus", "equals", "lbracket", "rbracket", "backslash", "semicolon", "quote", "comma", "period", "slash",
    ];

    /// <summary>层里能变成的键(目标键):导航与编辑键在前,F 键在后。</summary>
    private static readonly string[] TargetKeys =
    [
        "up", "down", "left", "right", "home", "end", "pageup", "pagedown",
        "backspace", "delete", "enter", "esc", "tab", "insert", "apps", "printscreen",
        .. Enumerable.Range(1, 24).Select(i => "f" + i),
    ];

    private static readonly Dictionary<string, string> Meanings = new(StringComparer.Ordinal)
    {
        ["up"] = "上", ["down"] = "下", ["left"] = "左", ["right"] = "右", ["home"] = "行首", ["end"] = "行尾",
        ["pageup"] = "上翻页", ["pagedown"] = "下翻页", ["backspace"] = "退格", ["delete"] = "删除", ["enter"] = "回车",
        ["esc"] = "Esc", ["apps"] = "右键菜单", ["printscreen"] = "截屏",
    };

    /// <summary>KeyChordText 只区分到"Ctrl",改键时要区分左右。</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["lctrl"] = "左 Ctrl", ["rctrl"] = "右 Ctrl", ["lshift"] = "左 Shift", ["rshift"] = "右 Shift",
        ["lalt"] = "左 Alt", ["ralt"] = "右 Alt", ["lwin"] = "左 Win", ["rwin"] = "右 Win", ["capslock"] = "CapsLock",
    };

    private readonly FeatureSettingsContext _context;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private KeyMappingSettings _value;

    private KeyMappingSettingsView(FeatureSettingsContext context)
    {
        _context = context;
        _value = context.Settings.Get<KeyMappingSettings>();
        Build();
    }

    public static UIElement Create(FeatureSettingsContext context) => new KeyMappingSettingsView(context)._host;

    private SuperCapsSettings Caps => _value.SuperCaps ??= new SuperCapsSettings();

    private void Build()
    {
        var caps = Caps;
        caps.Mappings ??= [];
        var mappingCards = new StackPanel { Spacing = 4, Visibility = Visible(caps.Enabled) };
        foreach (var mapping in caps.Mappings.Where(m => m is not null).ToList())
        {
            mappingCards.Children.Add(MappingCard(mapping));
        }

        mappingCards.Children.Add(AddCard());
        mappingCards.Children.Add(SettingsUi.Card(
            "恢复默认映射", "W A S D = ↑ ← ↓ →,Q / E = PgUp / PgDn,X / C = Home / End",
            SettingsUi.Button("恢复默认", () =>
            {
                Caps.Mappings = SuperCapsSettings.Defaults();
                SaveAndRebuild();
            })));

        _host.Content = SettingsUi.Stack([
            SettingsUi.Section("Super Caps"),
            SettingsUi.Card(
                "Super Caps",
                "把 CapsLock 变成换层键:按住它,下面的键变成方向键等。CapsLock 本身完全失效,不再切换大小写(需要大写时按住 Shift;启用时大写锁定若开着会自动关掉)",
                SettingsUi.Toggle(caps.Enabled, on =>
                {
                    Caps.Enabled = on;
                    SaveAndRebuild(); // 规则区里"CapsLock 规则不生效"的提示随之变化
                })),
            SettingsUi.Card(
                "可以和修饰键一起用",
                "Caps + Shift + D = 向右选中 · Caps + Ctrl + A = 向左跳一个词 · Caps + Shift + X = 选到行首。没有映射的键按 Caps 时照常输入"),
            mappingCards,
            .. RuleSection(),
            SettingsUi.Section("其他"),
            SettingsUi.Card(
                "键盘不好用时",
                "\"暂停/恢复按键映射\"会临时停用全部映射(包括 Super Caps),可以在上方给它设一个快捷键;也可以从托盘退出 WAIO"),
        ]);
    }

    private Border MappingCard(LayerKeySettings mapping)
    {
        var from = mapping.From ?? "";
        var target = SettingsUi.Choice(Options(TargetKeys, mapping.To), mapping.To ?? "", to =>
        {
            mapping.To = to;
            Save();
        });
        var remove = RemoveButton(() =>
        {
            Caps.Mappings.Remove(mapping);
            SaveAndRebuild();
        });

        return SettingsUi.Card($"Caps + {KeyChordText.Key(from)}", null, Row(target, remove));
    }

    private Border AddCard()
    {
        var used = Caps.Mappings.Where(m => m?.From is not null).Select(m => m.From!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = SourceKeys.Where(k => !used.Contains(k)).ToArray();
        if (free.Length == 0)
        {
            return SettingsUi.Card("添加按键", "可选的键都已经映射了");
        }

        var from = free[0];
        var to = TargetKeys[0];
        var fromBox = SettingsUi.Choice(Options(free, null), from, x => from = x);
        fromBox.MinWidth = 100;
        var toBox = SettingsUi.Choice(Options(TargetKeys, null), to, x => to = x);
        var add = SettingsUi.Button("添加", () =>
        {
            Caps.Mappings.Add(new LayerKeySettings { From = from, To = to });
            SaveAndRebuild();
        });

        return SettingsUi.Card("添加按键", "按住 Caps 时,左边的键变成右边的键", Row(fromBox, new TextBlock
        {
            Text = "→",
            VerticalAlignment = VerticalAlignment.Center,
        }, toBox, add));
    }

    /// <summary>"删除"图标按钮。</summary>
    private static Button RemoveButton(Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 }, // Delete
            Padding = new Thickness(8),
        };
        ToolTipService.SetToolTip(button, "删除");
        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    /// <summary>下拉选项;配置里手写的、不在常用列表里的值也放进去,避免显示成别的键。</summary>
    private static (string, string)[] Options(IEnumerable<string> keys, string? current)
    {
        var list = keys.ToList();
        if (!string.IsNullOrEmpty(current) && !list.Contains(current))
        {
            list.Insert(0, current);
        }

        return [.. list.Select(k => (k, Label(k)))];
    }

    private static string Label(string key)
    {
        if (Names.TryGetValue(key, out var name))
        {
            return name;
        }

        var text = KeyChordText.Key(key);
        return Meanings.TryGetValue(key, out var meaning) && meaning != text ? $"{text}  {meaning}" : text;
    }

    private void SaveAndRebuild()
    {
        Save();
        Build();
    }

    /// <summary>立即保存。保存失败只记日志:界面上的值保留,下次改动会再尝试保存。</summary>
    private void Save() => _ = SaveAsync(_value);

    private async Task SaveAsync(KeyMappingSettings value)
    {
        try
        {
            await _context.Settings.SaveAsync(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Logger.LogError(ex, "保存按键映射设置失败");
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
