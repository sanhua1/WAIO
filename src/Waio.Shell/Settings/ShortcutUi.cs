using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Core.Bindings;
using Waio.Kit.Commands;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>快捷键页和功能页共用:一条绑定怎么显示、状态徽标、"点键帽直接录制"的按钮(ADR-0011)。</summary>
internal static class ShortcutUi
{
    public static readonly CommandId OpenApp = "system.openApp";
    public static readonly CommandId OpenUrl = "system.openUrl";

    /// <summary>标题:打开程序/网页的绑定显示目标("打开 notepad"),其余显示命令名。</summary>
    public static string Title(ShellContext context, CommandBinding binding)
    {
        if (binding.Command == OpenApp && Argument(binding, "target") is { } target)
        {
            return "打开 " + Path.GetFileNameWithoutExtension(target.TrimEnd('\\', '/'));
        }

        if (binding.Command == OpenUrl && Argument(binding, "url") is { } url)
        {
            return "打开 " + (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url);
        }

        return context.DescriptorOf(binding.Command)?.Title ?? binding.Command.Value;
    }

    /// <summary>第二行:功能名 · 命令名(打开程序显示完整路径),再加上生效范围。</summary>
    public static string Detail(ShellContext context, CommandBinding binding)
    {
        var parts = new List<string>();
        if (binding.Command == OpenApp && Argument(binding, "target") is { } target)
        {
            parts.Add(target);
        }
        else if (binding.Command == OpenUrl && Argument(binding, "url") is { } url)
        {
            parts.Add(url);
        }
        else
        {
            parts.Add($"{context.OwnerOf(binding.Command)?.DisplayName ?? "未知功能"} · {binding.Command}");
        }

        if (binding.Scope is ProcessScope scope)
        {
            parts.Add(ScopeText(scope));
        }

        if (binding.Arguments.Values.TryGetValue("runElevated", out var elevated) && elevated.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            parts.Add("⚠ 以管理员运行"); // ADR-0006:提权必须醒目标注
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>状态徽标(没问题时返回 null)。优先级:停用 > 不可用 > 冲突 > 覆盖系统快捷键。</summary>
    public static FrameworkElement? Status(ShellContext context, CommandBinding binding)
    {
        (string Text, SettingsUi.Tone Tone, string Tip)? status =
            !binding.Enabled ? ("已停用", SettingsUi.Tone.Neutral, "这条快捷键已停用")
            : !context.IsCommandAvailable(binding.Command) ? ("不可用", SettingsUi.Tone.Caution, "提供这个命令的功能没有启用")
            : binding.Trigger is HotkeyTrigger { Chord.Hold: { } hold } && !context.CustomModifiers.Contains(hold)
                ? ("未生效", SettingsUi.Tone.Caution, $"{KeyChordText.Key(hold)} 不是自定义修饰键,在本页顶部添加后才会生效")
            : context.Conflicts.Any(c => c.First.Id == binding.Id || c.Second.Id == binding.Id)
                ? ("冲突", SettingsUi.Tone.Critical, "另一条启用的快捷键用了相同的组合,先保存的那条生效")
            : SystemShortcutUsages.For(binding, context.IsCommandAvailable) is { } usage ? SystemStatus(usage)
            : null;
        if (status is not { } s)
        {
            return null;
        }

        var badge = SettingsUi.Badge(s.Text, s.Tone);
        ToolTipService.SetToolTip(badge, s.Tip);
        return badge;
    }

    /// <summary>生效范围的说明,如"仅 code"、"除 game 外"。</summary>
    public static string ScopeText(ProcessScope scope) =>
        (scope.Exclude ? "除 " : "仅 ") + string.Join("、", scope.ProcessNames) + (scope.Exclude ? " 外" : "");

    /// <summary>
    /// 占用了系统快捷键时的徽标(ADR-0029):鼠标悬停显示这个组合的系统默认功能,以及怎么恢复。
    /// </summary>
    public static (string Text, SettingsUi.Tone Tone, string Tip) SystemStatus(SystemShortcutUsage usage)
    {
        var s = usage.Shortcut;
        var head = $"系统默认:{s.Function}({s.Category})" + (s.Note is { } note ? $"\n{note}" : "");
        return usage.State switch
        {
            SystemShortcutState.Clash => ("系统保留", SettingsUi.Tone.Critical,
                head + "\nWAIO 接管不了这个组合:按下时系统功能照样触发。请换一个组合。"),
            SystemShortcutState.PartlyTakenOver => ("系统快捷键", SettingsUi.Tone.Neutral,
                head + "\n只在生效范围内的程序里由 WAIO 接管,其他程序里系统功能照常。"),
            _ => ("系统快捷键", SettingsUi.Tone.Neutral,
                head + "\nWAIO 运行时由 WAIO 接管,系统功能暂停;停用 / 删除这条快捷键或退出 WAIO 后自动恢复。"),
        };
    }

    /// <summary>一排键帽(只显示)。</summary>
    public static StackPanel KeyCaps(KeyChord chord)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in KeyChordText.Parts(chord))
        {
            caps.Children.Add(SettingsUi.KeyCap(part));
        }

        return caps;
    }

    /// <summary>
    /// 直接录制并保存的键帽按钮(功能页用,参考 MAIO 的 FeatureShortcutsSection):
    /// 录到新组合就改这条绑定(没有则新建),清除就删除。
    /// </summary>
    public static HotkeyRecorder Recorder(ShellContext context, CommandBinding? binding, CommandId command, XamlRoot? root)
    {
        var chord = binding?.Trigger is HotkeyTrigger { Chord: var c } ? c : (KeyChord?)null;
        return new HotkeyRecorder(chord, context.RecordHotkeyAsync, recorded => _ = SaveRecordedAsync(context, binding, command, recorded, root));
    }

    /// <summary>执行一个改动绑定文件的操作;写文件失败时弹框说明(不让异常变成无人观察的崩溃)。</summary>
    public static async Task RunAsync(XamlRoot? root, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (IOException ex)
        {
            if (root is not null)
            {
                await new ContentDialog
                {
                    Title = "保存快捷键失败",
                    Content = ex.Message,
                    CloseButtonText = "知道了",
                    XamlRoot = root,
                }.ShowAsync();
            }
        }
    }

    private static Task SaveRecordedAsync(
        ShellContext context, CommandBinding? binding, CommandId command, KeyChord? recorded, XamlRoot? root) =>
        RunAsync(root, async () =>
        {
            if (recorded is not { } chord)
            {
                if (binding is not null)
                {
                    await context.DeleteBindingAsync(binding.Id);
                }

                return;
            }

            // 系统保留键(Win+L、Ctrl+Alt+Del)WAIO 拦不住,绑上也会和系统一起触发:不保存,说明原因(ADR-0029)
            if (KnownSystemShortcuts.Find(chord) is { CanOverride: false } reserved)
            {
                if (root is not null)
                {
                    await new ContentDialog
                    {
                        Title = $"{KeyChordText.Display(chord)} 是系统保留快捷键",
                        Content = $"系统默认:{reserved.Function}\n{reserved.Note}\n\nWAIO 接管不了它,请换一个组合。",
                        CloseButtonText = "知道了",
                        XamlRoot = root,
                    }.ShowAsync();
                }

                context.ReloadBindings(); // 触发刷新,让键帽回到原来的组合
                return;
            }

            await context.SaveBindingAsync((binding ?? BindingEditorDialog.NewBinding(command)) with { Trigger = new HotkeyTrigger(chord) });
        });

    private static string? Argument(CommandBinding binding, string name) =>
        binding.Arguments.Values.TryGetValue(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
}
