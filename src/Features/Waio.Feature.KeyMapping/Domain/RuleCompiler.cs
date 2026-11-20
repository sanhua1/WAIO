namespace Waio.Feature.KeyMapping.Domain;

/// <param name="RuleErrors">rules 里第几条(从 0 开始)→ 它为什么被忽略(不带"第 N 条规则被忽略"前缀)。设置页在对应卡片上显示。</param>
internal sealed record CompileResult(RuleSet Rules, IReadOnlyList<string> Errors, IReadOnlyDictionary<int, string> RuleErrors);

/// <summary>
/// 把用户配置的规则编译成 <see cref="RuleSet"/>。配置是不可信输入(ADR-0004):
/// 不合法的规则只丢弃该条并报告原因,其余照常生效;绝不抛异常。
/// </summary>
internal static class RuleCompiler
{
    public const int MaxRules = 256;

    /// <summary>Super Caps 的换层键。</summary>
    public const string SuperCapsKey = "capslock";

    /// <param name="normalizeKey">键名规范化(来自 IInputService.NormalizeKey):未知返回 null。</param>
    public static CompileResult Compile(KeyMappingSettings? settings, Func<string, string?> normalizeKey)
    {
        var errors = new List<string>();
        var rules = new List<Rule>();
        var seen = new HashSet<string>();
        var entries = settings?.Rules ?? [];
        var superCaps = CompileSuperCaps(settings?.SuperCaps, normalizeKey, errors);
        var ruleErrors = new Dictionary<int, string>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (rules.Count >= MaxRules)
            {
                errors.Add($"规则超过 {MaxRules} 条的上限,后面的都被忽略");
                break;
            }

            var label = $"第 {i + 1} 条规则";
            var before = errors.Count;
            var rule = TryCompile(entries[i], normalizeKey, label, errors);
            if (rule is not null && superCaps is not null && rule.From == SuperCapsKey)
            {
                errors.Add($"{label}被忽略:CapsLock 已用作 Super Caps 的换层键(要用这条规则,先关掉 Super Caps)");
            }
            else if (rule is not null && !seen.Add(rule.From))
            {
                errors.Add($"{label}被忽略:键 '{rule.From}' 已有规则(同一个键只能有一条)");
            }
            else if (rule is not null)
            {
                rules.Add(rule);
            }

            if (errors.Count > before)
            {
                var prefix = label + "被忽略:";
                var text = errors[before];
                ruleErrors[i] = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
            }
        }

        return new CompileResult(new RuleSet(rules, superCaps), errors, ruleErrors);
    }

    /// <summary>Super Caps 层。关闭时返回 null;启用但一个映射都没有时仍返回层(CapsLock 照样被吞,只是没有映射)。</summary>
    private static Layer? CompileSuperCaps(SuperCapsSettings? settings, Func<string, string?> normalizeKey, List<string> errors)
    {
        if (settings is not { Enabled: true })
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = settings.Mappings ?? [];
        for (var i = 0; i < entries.Count && map.Count < MaxRules; i++)
        {
            var label = $"Super Caps 第 {i + 1} 个映射";
            if (entries[i] is not { } entry)
            {
                continue;
            }

            var from = Key(entry.From, normalizeKey, label, "from", errors, out var fromOk);
            var to = Key(entry.To, normalizeKey, label, "to", errors, out var toOk);
            if (!fromOk || !toOk)
            {
                continue;
            }

            if (from == SuperCapsKey || IsModifier(from!))
            {
                errors.Add($"{label}被忽略:'{from}' 不能放进层里(CapsLock 和修饰键保持原样,才能组合出 Shift+方向键这类操作)");
            }
            else if (to == SuperCapsKey)
            {
                errors.Add($"{label}被忽略:不能映射成 CapsLock");
            }
            else if (!map.TryAdd(from!, to!))
            {
                errors.Add($"{label}被忽略:'{from}' 已经映射过了");
            }
        }

        return new Layer(SuperCapsKey, map);
    }

    private static bool IsModifier(string key) =>
        key is "lctrl" or "rctrl" or "lshift" or "rshift" or "lalt" or "ralt" or "lwin" or "rwin";

    private static Rule? TryCompile(KeyRuleSettings? entry, Func<string, string?> normalizeKey, string label, List<string> errors)
    {
        if (entry is null)
        {
            errors.Add($"{label}被忽略:内容为空");
            return null;
        }

        var from = Key(entry.From, normalizeKey, label, "from", errors, out var fromOk);
        if (!fromOk)
        {
            return null;
        }

        var hasTo = !string.IsNullOrWhiteSpace(entry.To);
        var hasTap = !string.IsNullOrWhiteSpace(entry.Tap);
        var hasHold = !string.IsNullOrWhiteSpace(entry.Hold);

        if (hasTo && (hasTap || hasHold))
        {
            errors.Add($"{label}被忽略:'to' 与 'tap'/'hold' 不能同时出现");
            return null;
        }

        if (hasTo)
        {
            var to = Key(entry.To, normalizeKey, label, "to", errors, out var toOk);
            if (!toOk)
            {
                return null;
            }

            if (to == from)
            {
                errors.Add($"{label}被忽略:'{from}' 改成它自己没有意义");
                return null;
            }

            return new Rule { From = from!, To = to };
        }

        if (hasTap && hasHold)
        {
            var tap = Key(entry.Tap, normalizeKey, label, "tap", errors, out var tapOk);
            var hold = Key(entry.Hold, normalizeKey, label, "hold", errors, out var holdOk);
            return tapOk && holdOk ? new Rule { From = from!, Tap = tap, Hold = hold } : null;
        }

        errors.Add($"{label}被忽略:需要 'to',或者同时有 'tap' 和 'hold'");
        return null;
    }

    private static string? Key(string? name, Func<string, string?> normalizeKey, string label, string field, List<string> errors, out bool ok)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? null : normalizeKey(name);
        ok = normalized is not null;
        if (!ok)
        {
            errors.Add($"{label}被忽略:'{field}' 的键名 '{name}' 无法识别");
        }

        return normalized;
    }
}
