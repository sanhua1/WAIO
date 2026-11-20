using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 快捷键总表(ADR-0011,参考 MAIO 的 ShortcutsPane):按功能分组列出所有绑定;
/// 每行:启用开关、键帽(点击直接录制)、标题与说明、状态徽标、更多菜单(编辑/删除)。
/// </summary>
internal sealed partial class BindingsPage : Page
{
    private ShellContext _context = null!;

    public BindingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (ShellContext)e.Parameter;
        _context.BindingsChanged += OnBindingsChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _context.BindingsChanged -= OnBindingsChanged;

    private void OnBindingsChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        EngineBar.IsOpen = !_context.IsInputRunning;
        ModifiersHost.Content = CustomModifiersCard.Build(_context, XamlRoot);
        ErrorBar.IsOpen = _context.Bindings.Errors.Count > 0;
        ErrorBar.Message = string.Join(Environment.NewLine, _context.Bindings.Errors)
            + Environment.NewLine + "在这里保存任何改动时,这些条目会从文件中移除(原配置文件已备份)。";

        var query = Search.Text.Trim();
        var bindings = _context.Bindings.Bindings.Where(b => Matches(b, query)).ToList();
        Groups.Children.Clear();

        // 按功能分组,顺序同设置窗口左栏;找不到所属功能的放最后
        var owners = _context.Manifests.Cast<FeatureManifest?>().Append(null);
        foreach (var owner in owners)
        {
            var group = bindings.Where(b => _context.OwnerOf(b.Command)?.Id == owner?.Id).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            Groups.Children.Add(SettingsUi.Section(owner?.DisplayName ?? "未知命令"));
            foreach (var binding in group)
            {
                Groups.Children.Add(Row(binding, owner));
            }
        }

        EmptyState.Visibility = bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = query.Length > 0 ? "没有匹配的快捷键" : "还没有快捷键,点右上角\"添加快捷键\"";
    }

    private bool Matches(CommandBinding binding, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        var chord = binding.Trigger is HotkeyTrigger { Chord: var c } ? KeyChordText.Display(c) + " " + c : "";
        return $"{chord} {ShortcutUi.Title(_context, binding)} {ShortcutUi.Detail(_context, binding)}"
            .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private Border Row(CommandBinding binding, FeatureManifest? owner)
    {
        var toggle = new ToggleSwitch { IsOn = binding.Enabled, OnContent = "", OffContent = "", MinWidth = 0, Width = 48 };
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn != binding.Enabled)
            {
                _ = ShortcutUi.RunAsync(XamlRoot, () => _context.SaveBindingAsync(binding with { Enabled = toggle.IsOn }));
            }
        };

        var icon = new FontIcon { Glyph = owner?.IconGlyph ?? "\uE765", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = ShortcutUi.Title(_context, binding), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(SettingsUi.Hint(ShortcutUi.Detail(_context, binding)));
        ((TextBlock)text.Children[1]).FontSize = 12;
        ((TextBlock)text.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;

        var recorder = ShortcutUi.Recorder(_context, binding, binding.Command, XamlRoot).View;
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 14 }, Background = null, BorderThickness = new Thickness(0) };
        more.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("编辑…", "\uE70F", () => _ = EditAsync(binding)),
                MenuItem("删除", "\uE74D", () => _ = ShortcutUi.RunAsync(XamlRoot, () => _context.DeleteBindingAsync(binding.Id))),
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
        if (ShortcutUi.Status(_context, binding) is { } status)
        {
            Place(grid, status, 3);
        }

        Place(grid, recorder, 4);
        Place(grid, more, 5);

        var card = SettingsUi.Card("", null, null);
        card.Child = grid;
        card.Opacity = binding.Enabled ? 1 : 0.6;
        card.DoubleTapped += (_, e) => OnRowDoubleTapped(binding, e);
        return card;
    }

    private void OnRowDoubleTapped(CommandBinding binding, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _ = EditAsync(binding);
    }

    private Task EditAsync(CommandBinding binding) =>
        ShortcutUi.RunAsync(XamlRoot, () => BindingEditorDialog.EditAsync(_context, binding, isNew: false, XamlRoot));

    private void OnAdd(object sender, RoutedEventArgs e) =>
        _ = ShortcutUi.RunAsync(XamlRoot, () => BindingEditorDialog.EditAsync(
            _context, BindingEditorDialog.NewBinding(ShortcutUi.OpenApp), isNew: true, XamlRoot));

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void OnReload(object sender, RoutedEventArgs e) => _context.ReloadBindings();

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_context.Paths.ConfigDirectory);
        await _context.OpenAsync(_context.Paths.ConfigDirectory);
    }

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
