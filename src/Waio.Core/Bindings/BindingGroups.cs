using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

/// <summary>"WAIO 快捷键"页列表里的一组。</summary>
/// <param name="Id">功能 id、需要参数的命令 id,或 <see cref="BindingGroups.UnknownId"/>。</param>
/// <param name="Title">组标题:功能名,或命令名(如"打开程序或文件")。</param>
/// <param name="Glyph">所属功能的图标。</param>
/// <param name="IsFeature">是不是"一个功能的快捷键"这一类(标题用强调色图标)。</param>
public sealed record BindingGroup(string Id, string Title, string Glyph, bool IsFeature, IReadOnlyList<CommandBinding> Bindings);

/// <summary>
/// 快捷键总表的分组(参考 MAIO 的 ShortcutGroup)。纯逻辑,界面只负责显示。
/// 顺序:各功能(按功能目录的顺序)→ 找不到所属功能的 → 需要参数的命令各成一组(如"打开程序或文件""打开网页")。
/// 需要参数的命令单独成组,因为同一个命令往往有很多条(每个程序一条),和功能自己的快捷键混在一起不好找。
/// 组内保持绑定原来的顺序;没有绑定的组不出现。
/// </summary>
public static class BindingGroups
{
    public const string UnknownId = "unknown";

    public static IReadOnlyList<BindingGroup> Make(IEnumerable<CommandBinding> bindings, IReadOnlyList<FeatureManifest> manifests)
    {
        var list = bindings.ToList();
        var groups = new List<BindingGroup>();
        var parameterized = new List<BindingGroup>();
        var known = new HashSet<CommandId>();

        foreach (var manifest in manifests)
        {
            var simple = manifest.Commands.Where(c => !NeedsArguments(c)).Select(c => c.Id).ToHashSet();
            known.UnionWith(manifest.Commands.Select(c => c.Id));
            Add(groups, new BindingGroup(manifest.Id.Value, manifest.DisplayName, manifest.IconGlyph, true,
                [.. list.Where(b => simple.Contains(b.Command))]));

            foreach (var command in manifest.Commands.Where(NeedsArguments))
            {
                Add(parameterized, new BindingGroup(command.Id.Value, command.Title, manifest.IconGlyph, false,
                    [.. list.Where(b => b.Command == command.Id)]));
            }
        }

        Add(groups, new BindingGroup(UnknownId, "未知命令", "", false, [.. list.Where(b => !known.Contains(b.Command))]));
        groups.AddRange(parameterized);
        return groups;
    }

    /// <summary>需要填参数的命令(打开程序、打开网页等):一个命令通常有很多条绑定。</summary>
    public static bool NeedsArguments(CommandDescriptor command) => command.Parameters.Any(p => p.Required);

    private static void Add(List<BindingGroup> groups, BindingGroup group)
    {
        if (group.Bindings.Count > 0)
        {
            groups.Add(group);
        }
    }
}
