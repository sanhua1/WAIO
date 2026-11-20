using Waio.Kit.Input;

namespace Waio.Core.Input;

/// <summary>
/// 快捷键匹配器:启用的热键绑定建成 [KeyChord → 绑定列表] 字典,按键时 O(1) 查找 + 作用域过滤。
/// 不可变:改绑定时在后台构建新实例,原子替换给钩子线程(ARCHITECTURE §5.2 第 2 条)。
/// 同一快捷键有多条绑定时(冲突),按列表顺序取第一个作用域命中的。
/// </summary>
public sealed class BindingMatcher
{
    public static BindingMatcher Empty { get; } = new(new Dictionary<KeyChord, CommandBinding[]>());

    private readonly Dictionary<KeyChord, CommandBinding[]> _byChord;

    private BindingMatcher(Dictionary<KeyChord, CommandBinding[]> byChord) => _byChord = byChord;

    public int ChordCount => _byChord.Count;

    public static BindingMatcher Build(IEnumerable<CommandBinding> bindings)
    {
        var map = new Dictionary<KeyChord, List<CommandBinding>>();
        foreach (var binding in bindings)
        {
            if (!binding.Enabled || binding.Trigger is not HotkeyTrigger hotkey)
            {
                continue;
            }

            // 规范化键名,让 "Escape"、"esc" 落到同一个键上
            var chord = KeyNames.Canonicalize(hotkey.Chord);
            if (!map.TryGetValue(chord, out var list))
            {
                map[chord] = list = [];
            }

            list.Add(binding);
        }

        return new BindingMatcher(map.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    }

    /// <param name="foregroundProcess">前台进程名(不含 .exe),未知为 null。</param>
    public CommandBinding? Match(KeyChord chord, string? foregroundProcess)
    {
        if (!_byChord.TryGetValue(chord, out var candidates))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (InScope(candidate.Scope, foregroundProcess))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool InScope(BindingScope scope, string? foregroundProcess)
    {
        if (scope is not ProcessScope process)
        {
            return true;
        }

        var name = Normalize(foregroundProcess);
        var listed = name is not null && process.ProcessNames.Any(n => string.Equals(Normalize(n), name, StringComparison.OrdinalIgnoreCase));

        // 进程未知时:"只在这些进程里"不生效,"除这些进程外"生效
        return process.Exclude ? !listed : listed;
    }

    private static string? Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
