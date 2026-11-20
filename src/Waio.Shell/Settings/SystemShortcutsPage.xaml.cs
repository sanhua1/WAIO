using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Core.Bindings;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 系统快捷键页(ADR-0029):按分组列出 Windows 默认快捷键,标出哪些被 WAIO 占用,
/// 被占用的可以"恢复系统功能"(停用占用它的 WAIO 快捷键)。
/// </summary>
internal sealed partial class SystemShortcutsPage : Page
{
    private ShellContext _context = null!;

    public SystemShortcutsPage()
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

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        EngineBar.IsOpen = !_context.IsInputRunning;

        var usages = _context.SystemShortcutUsage();
        var taken = usages.Count(u => u.State != SystemShortcutState.System);
        Summary.Text = taken == 0
            ? $"共 {usages.Count} 个系统快捷键,WAIO 一个都没有占用。"
            : $"共 {usages.Count} 个系统快捷键,其中 {taken} 个被 WAIO 占用。";

        var query = Search.Text.Trim();
        var shown = usages.Where(u => (!OnlyTaken.IsOn || u.State != SystemShortcutState.System) && Matches(u, query)).ToList();

        Groups.Children.Clear();
        foreach (var category in KnownSystemShortcuts.Categories)
        {
            var rows = shown.Where(u => u.Shortcut.Category == category).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            Groups.Children.Add(SettingsUi.Section(category));
            Groups.Children.Add(SettingsUi.Stack([.. rows.Select(Row)]));
        }

        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = OnlyTaken.IsOn && query.Length == 0 ? "WAIO 没有占用任何系统快捷键" : "没有匹配的系统快捷键";
    }

    private bool Matches(SystemShortcutUsage usage, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        var s = usage.Shortcut;
        var owners = string.Join(" ", usage.Bindings.Select(b => ShortcutUi.Title(_context, b)));
        var text = $"{s.Chord} {KeyChordText.Display(s.Chord)} {s.Function} {s.Category} {s.Note} {owners}";
        return text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || text.Replace(" ", "", StringComparison.Ordinal).Contains(query.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    private Border Row(SystemShortcutUsage usage)
    {
        var s = usage.Shortcut;
        var caps = ShortcutUi.KeyCaps(s.Chord);
        caps.Width = 230;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = s.Function, TextWrapping = TextWrapping.Wrap });
        if (Detail(usage) is { } detail)
        {
            var hint = SettingsUi.Hint(detail);
            hint.FontSize = 12;
            hint.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(hint);
        }

        var grid = new Grid { ColumnSpacing = 14 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        Place(grid, caps, 0);
        Place(grid, text, 1);
        if (Badge(usage) is { } badge)
        {
            Place(grid, badge, 2);
        }

        if (usage.State != SystemShortcutState.System)
        {
            var restore = SettingsUi.Button("恢复系统功能", () => _ = ShortcutUi.RunAsync(XamlRoot, () => _context.RestoreSystemShortcutAsync(s.Chord)));
            ToolTipService.SetToolTip(restore, "停用占用这个组合的 WAIO 快捷键(不删除,可以在\"快捷键\"页重新打开)");
            Place(grid, restore, 3);
        }

        var card = SettingsUi.Card("", null, null);
        card.Child = grid;
        return card;
    }

    /// <summary>第二行:谁占用了它;没被占用时显示补充说明。</summary>
    private string? Detail(SystemShortcutUsage usage)
    {
        if (usage.Bindings.Count == 0)
        {
            return usage.Shortcut.Note;
        }

        var owners = string.Join("、", usage.Bindings.Select(b =>
            $"「{ShortcutUi.Title(_context, b)}」" + (b.Scope is Waio.Kit.Input.ProcessScope scope ? $"({ShortcutUi.ScopeText(scope)})" : "")));
        return $"被 WAIO 的 {owners} 占用";
    }

    private static Border? Badge(SystemShortcutUsage usage)
    {
        (string Text, SettingsUi.Tone Tone, string Tip)? badge = usage.State switch
        {
            SystemShortcutState.TakenOver => ("WAIO 接管", SettingsUi.Tone.Caution, "WAIO 运行时按这个组合执行 WAIO 的命令,系统功能暂停"),
            SystemShortcutState.PartlyTakenOver => ("部分接管", SettingsUi.Tone.Neutral, "只在指定的程序里由 WAIO 接管,其他程序里系统功能照常"),
            SystemShortcutState.Clash => ("冲突", SettingsUi.Tone.Critical, "这是系统保留键,WAIO 拦不住:按下时系统功能和 WAIO 命令都会触发。请给那条快捷键换个组合"),
            _ when !usage.Shortcut.CanOverride => ("系统保留", SettingsUi.Tone.Neutral, usage.Shortcut.Note ?? "WAIO 接管不了这个组合"),
            _ => null,
        };
        if (badge is not { } b)
        {
            return null;
        }

        var view = SettingsUi.Badge(b.Text, b.Tone);
        ToolTipService.SetToolTip(view, b.Tip);
        return view;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        element.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
    }
}
