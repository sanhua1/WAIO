using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Core.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 快捷键页顶部的"自定义修饰键"卡片(ADR-0012):列出已声明的键(可移除),从候选键里添加新的。
/// </summary>
internal static class CustomModifiersCard
{
    public static Border Build(ShellContext context, XamlRoot? root)
    {
        var current = context.CustomModifiers;
        var action = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        foreach (var key in current)
        {
            var remove = new Button
            {
                Padding = new Thickness(6, 2, 4, 2),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { new TextBlock { Text = KeyChordText.Key(key), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new FontIcon { Glyph = "\uE711", FontSize = 10 } },
                },
            };
            ToolTipService.SetToolTip(remove, $"不再把 {KeyChordText.Key(key)} 当修饰键(用到它的快捷键会失效)");
            remove.Click += (_, _) => _ = ShortcutUi.RunAsync(root, () => context.SetCustomModifiersAsync(current.Where(k => k != key)));
            action.Children.Add(remove);
        }

        var candidates = KeyNames.CustomModifierCandidates.Where(k => !current.Contains(k)).ToList();
        var picker = new ComboBox { PlaceholderText = "添加…", MinWidth = 110, MaxDropDownHeight = 320 };
        foreach (var key in candidates)
        {
            picker.Items.Add(KeyChordText.Key(key));
        }

        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
            {
                var chosen = candidates[picker.SelectedIndex];
                _ = ShortcutUi.RunAsync(root, () => context.SetCustomModifiersAsync([.. current, chosen]));
            }
        };
        action.Children.Add(picker);

        return SettingsUi.Card(
            "自定义修饰键",
            current.Count == 0
                ? "把 F24 这类键当作额外的修饰键:按住它再按别的键组成快捷键(如 F24 + J),单独轻点时仍是它本身"
                : "按住这些键再按别的键组成快捷键(录制时先按住它);单独轻点时仍是它本身,对应的单键快捷键改为松开时触发",
            action);
    }
}
