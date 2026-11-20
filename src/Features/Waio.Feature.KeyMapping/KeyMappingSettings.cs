namespace Waio.Feature.KeyMapping;

/// <summary>
/// 按键映射设置(features/keymap.json)。字段只增不删。示例:
/// <code>
/// { "schemaVersion": 1,
///   "rules": [ { "from": "rctrl", "to": "apps" } ],
///   "superCaps": { "enabled": true, "mappings": [ { "from": "w", "to": "up" } ] } }
/// </code>
/// 配置是不可信输入:全部经 <see cref="Domain.RuleCompiler"/> 校验后才会生效。
/// </summary>
public sealed class KeyMappingSettings
{
    public int SchemaVersion { get; set; } = 1;

    public List<KeyRuleSettings> Rules { get; set; } = [];

    /// <summary>Super Caps 层(ADR-0013)。旧配置文件里没有这一项时按默认值(启用 + 默认映射)。</summary>
    public SuperCapsSettings SuperCaps { get; set; } = new();
}

/// <summary>
/// 一条规则。要么 <see cref="To"/>(简单改键),要么 <see cref="Tap"/> + <see cref="Hold"/>(双功能键),二选一。
/// </summary>
public sealed class KeyRuleSettings
{
    public string? From { get; set; }

    /// <summary>简单改键的目标键。</summary>
    public string? To { get; set; }

    /// <summary>双功能键:单独轻点时发出的键。</summary>
    public string? Tap { get; set; }

    /// <summary>双功能键:按住并配合其他键时当作的键(通常是修饰键)。</summary>
    public string? Hold { get; set; }
}

/// <summary>
/// Super Caps(ADR-0013):CapsLock 变成换层键。按住 CapsLock 时,<see cref="Mappings"/> 里的键变成目标键(如 W → ↑);
/// CapsLock 本身完全被吞掉,不再切换大写锁定。
/// </summary>
public sealed class SuperCapsSettings
{
    public bool Enabled { get; set; } = true;

    public List<LayerKeySettings> Mappings { get; set; } = Defaults();

    /// <summary>默认:W/A/S/D = 上/左/下/右,Q/E = PgUp/PgDn,X/C = Home/End。</summary>
    public static List<LayerKeySettings> Defaults() =>
    [
        new() { From = "w", To = "up" },
        new() { From = "a", To = "left" },
        new() { From = "s", To = "down" },
        new() { From = "d", To = "right" },
        new() { From = "q", To = "pageup" },
        new() { From = "e", To = "pagedown" },
        new() { From = "x", To = "home" },
        new() { From = "c", To = "end" },
    ];
}

/// <summary>层里的一个键:按住层键时,<see cref="From"/> 变成 <see cref="To"/>。</summary>
public sealed class LayerKeySettings
{
    public string? From { get; set; }

    public string? To { get; set; }
}
