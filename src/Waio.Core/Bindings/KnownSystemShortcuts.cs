using Waio.Core.Input;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

/// <summary>
/// 一个 Windows 默认快捷键。
/// </summary>
/// <param name="Chord">组合(规范写法)。</param>
/// <param name="Function">系统默认功能(给用户看的中文说明)。</param>
/// <param name="Category">分组(系统快捷键页按它分组)。</param>
/// <param name="CanOverride">
/// WAIO 能不能接管。false = 低级键盘钩子拦不住(Ctrl+Alt+Del、Win+L 由 winlogon 处理),
/// 或者是不该拦的救急键;这类组合不允许绑定(ADR-0029)。
/// </param>
/// <param name="Note">补充说明(什么条件下才有、为什么不能接管)。</param>
public sealed record SystemShortcut(KeyChord Chord, string Function, string Category, bool CanOverride = true, string? Note = null);

/// <summary>
/// WAIO 维护的 Windows 默认快捷键清单(ADR-0029)。来源:微软官方
/// "Keyboard shortcuts in Windows"(support.microsoft.com,2026-11 抓取)+ 任务栏数字键、游戏栏、微软拼音等补充。
/// 只收全局生效的系统快捷键;Ctrl+C 这类由各程序自己处理的不收。数据在 <c>KnownSystemShortcuts.Data.cs</c>。
/// </summary>
public static partial class KnownSystemShortcuts
{
    private static readonly IReadOnlyList<SystemShortcut> Items = Build();

    private static readonly Dictionary<KeyChord, SystemShortcut> ByChord = Items.ToDictionary(s => s.Chord);

    /// <summary>全部条目,按分组、再按清单里的顺序。</summary>
    public static IReadOnlyList<SystemShortcut> All => Items;

    /// <summary>分组名,按显示顺序。</summary>
    public static IReadOnlyList<string> Categories { get; } = [.. Items.Select(s => s.Category).Distinct()];

    /// <summary>按组合查系统快捷键(组合会先规范化;带自定义修饰键的组合永远不是系统快捷键)。</summary>
    public static SystemShortcut? Find(KeyChord chord) =>
        chord.Hold is null ? ByChord.GetValueOrDefault(KeyNames.Canonicalize(chord)) : null;

    /// <summary>系统默认功能;不是系统快捷键返回 null。</summary>
    public static string? Describe(KeyChord chord) => Find(chord)?.Function;

    private static List<SystemShortcut> Build()
    {
        var list = new List<SystemShortcut>();
        var category = "";

        void Group(string name) => category = name;
        void Add(string chord, string function, string? note = null) =>
            list.Add(new SystemShortcut(KeyNames.Canonicalize(KeyChord.Parse(chord)), function, category, true, note));
        void Reserved(string chord, string function, string note) =>
            list.Add(new SystemShortcut(KeyNames.Canonicalize(KeyChord.Parse(chord)), function, category, false, note));

        Fill(Group, Add, Reserved);

        var duplicate = list.GroupBy(s => s.Chord).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"系统快捷键清单里 {duplicate.Key} 重复了");
        }

        return list; // 数据按分组连续书写,顺序即显示顺序
    }
}
