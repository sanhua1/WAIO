using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;

namespace Waio.Core.Bindings;

/// <param name="NewBindings">要追加到 bindings.json 的绑定。</param>
/// <param name="SeededFeatures">第一次处理的功能,要记入 app.json 的 <c>seededFeatures</c>。</param>
/// <param name="SeededBindings">这次处理过的默认绑定(包括被跳过的)的键,要记入 app.json 的 <c>seededBindings</c>,以后不再处理。</param>
/// <param name="Skipped">被跳过的默认绑定及原因,写日志用。</param>
public sealed record SeedResult(
    IReadOnlyList<CommandBinding> NewBindings, IReadOnlyList<string> SeededFeatures, IReadOnlyList<string> SeededBindings,
    IReadOnlyList<string> Skipped)
{
    public bool IsEmpty => SeededFeatures.Count == 0 && SeededBindings.Count == 0;
}

/// <summary>
/// 出厂默认绑定的落地(ADR-0022):<b>每一条默认绑定只处理一次</b>,按 <see cref="KeyOf"/> 记在 app.json 的 <c>seededBindings</c> 里。
/// <list type="bullet">
/// <item>功能第一次启用:它的默认绑定全部追加(键位已被用户占用的跳过,不抢用户的键);</item>
/// <item>功能以后新增的默认绑定(老用户升级后第一次见到):这个命令<b>还没有任何绑定</b>时才追加;
/// 用户已经给它绑了别的键,就尊重用户,只记下不追加;</item>
/// <item>处理过的(包括被跳过的)都记下:用户之后删除或修改了,不会被改回来。</item>
/// </list>
/// 以前(ADR-0022 之前)只按功能记 <c>seededFeatures</c>,功能后来加的默认键老配置永远收不到(截图的 Alt+F2、Alt+F3)。纯逻辑,可单测。
/// </summary>
public static class DefaultBindingSeeder
{
    /// <summary>一条默认绑定的身份:命令 + 触发方式(如 "screenshot.record|alt+f3")。默认键位改了算一条新的默认绑定。</summary>
    public static string KeyOf(DefaultBinding binding) => $"{binding.Command.Value}|{Describe(binding.Trigger)}";

    public static SeedResult Seed(
        IEnumerable<FeatureManifest> enabledFeatures,
        IReadOnlyCollection<string> seededFeatures,
        IReadOnlyCollection<string> seededBindings,
        IReadOnlyList<CommandBinding> existing)
    {
        var added = new List<CommandBinding>();
        var features = new List<string>();
        var keys = new List<string>();
        var skipped = new List<string>();
        var taken = existing.Where(b => b.Enabled).ToList();
        var bound = existing.Select(b => b.Command.Value).ToHashSet(); // 包括禁用的:用户禁用也是一种选择

        foreach (var manifest in enabledFeatures)
        {
            var firstTime = !seededFeatures.Contains(manifest.Id.Value);
            if (firstTime)
            {
                features.Add(manifest.Id.Value);
            }

            foreach (var template in manifest.DefaultBindings)
            {
                var key = KeyOf(template);
                if (seededBindings.Contains(key) || keys.Contains(key))
                {
                    continue;
                }

                keys.Add(key);
                var binding = new CommandBinding(
                    Guid.NewGuid(), template.Trigger, template.Command, template.Arguments ?? CommandArguments.Empty, BindingScope.Global);

                // 功能以前播种过、这条是后来才有的(或者是旧版本没记录逐条状态的):用户已经给这个命令绑过键就不动
                if (!firstTime && bound.Contains(template.Command.Value))
                {
                    continue;
                }

                if (taken.FirstOrDefault(other => SameHotkey(other, binding)) is { } clash)
                {
                    skipped.Add($"{manifest.Id} 的默认绑定 {Describe(binding.Trigger)} 被跳过:已被绑定到 {clash.Command}");
                    continue;
                }

                added.Add(binding);
                taken.Add(binding);
                bound.Add(template.Command.Value);
            }
        }

        return new SeedResult(added, features, keys, skipped);
    }

    private static bool SameHotkey(CommandBinding a, CommandBinding b) =>
        a.Trigger is HotkeyTrigger x && b.Trigger is HotkeyTrigger y && x.Chord == y.Chord
        && BindingConflictDetector.ScopesOverlap(a.Scope, b.Scope);

    private static string Describe(Trigger trigger) =>
        trigger is HotkeyTrigger hotkey ? hotkey.Chord.ToString() : trigger.ToString() ?? "?";
}
