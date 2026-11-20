using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 一条绑定的行:启用开关、图标、标题与说明、状态徽标、键帽(点击直接录制)、更多菜单(编辑 / 删除)。
/// 双击整行打开编辑器。"WAIO 快捷键"页和功能页共用(同 MAIO 的 BindingRow)。
/// </summary>
internal static class BindingRow
{
    public static Border Build(ShellContext context, CommandBinding binding, string glyph, XamlRoot? root)
    {
        var toggle = new ToggleSwitch { IsOn = binding.Enabled, OnContent = "", OffContent = "", MinWidth = 0, Width = 48 };
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn != binding.Enabled)
            {
                _ = ShortcutUi.RunAsync(root, () => context.SaveBindingAsync(binding with { Enabled = toggle.IsOn }));
            }
        };

        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = ShortcutUi.Title(context, binding), TextTrimming = TextTrimming.CharacterEllipsis });
        if (ShortcutUi.Detail(context, binding) is { Length: > 0 } detail)
        {
            var hint = SettingsUi.Hint(detail);
            hint.FontSize = 12;
            hint.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(hint);
        }

        var recorder = ShortcutUi.Recorder(context, binding, binding.Command, root).View;
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Background = null, BorderThickness = new Thickness(0) };
        more.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("编辑…", "", () => _ = EditAsync(context, binding, root)),
                MenuItem("删除", "", () => _ = ShortcutUi.RunAsync(root, () => context.DeleteBindingAsync(binding.Id))),
            },
        };

        var grid = new Grid { ColumnSpacing = 14 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        Place(grid, toggle, 0);
        Place(grid, icon, 1);
        Place(grid, text, 2);
        if (ShortcutUi.Status(context, binding) is { } status)
        {
            Place(grid, status, 3);
        }

        Place(grid, recorder, 4);
        Place(grid, more, 5);

        var card = SettingsUi.Card("", null, null);
        card.Child = grid;
        card.Opacity = binding.Enabled ? 1 : 0.6;
        card.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            _ = EditAsync(context, binding, root);
        };
        return card;
    }

    /// <summary>打开编辑器(命令、参数、生效范围)。</summary>
    public static Task EditAsync(ShellContext context, CommandBinding binding, XamlRoot? root) =>
        ShortcutUi.RunAsync(root, () => BindingEditorDialog.EditAsync(context, binding, isNew: false, root!));

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => click();
        return item;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        element.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
    }
}
