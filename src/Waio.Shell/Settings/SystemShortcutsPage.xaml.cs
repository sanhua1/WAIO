using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Core.Bindings;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 系统快捷键页(ADR-0029,用词参考 MAIO 0.3.0):按分组列出 Windows 默认快捷键,标出哪些由 WAIO 接管,
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
        IntroHost.Content = SettingsUi.PageIntro(
            "", // Keyboard
            "WAIO 快捷键优先",
            "WAIO 的快捷键和系统快捷键相同时,WAIO 运行期间按下只会触发 WAIO 的功能,系统功能不会触发,这里显示为「由 WAIO 接管」。",
            "接管只在 WAIO 运行时有效,WAIO 不修改任何系统设置。",
            "停用或删除那条 WAIO 快捷键、关掉它所属的功能,或者退出 WAIO,系统快捷键立即恢复工作。",
            "想把某个组合还给系统,点那一行的「恢复系统功能」:占用它的 WAIO 快捷键会被停用(不删除,随时可以重新打开)。",
            "Win+L、Ctrl+Alt+Del 等系统保留键 WAIO 接管不了,标为「系统保留」。");
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
        var taken = usages.Count(u => u.State is SystemShortcutState.TakenOver or SystemShortcutState.PartlyTakenOver);
        Summary.Text = taken == 0
            ? $"共 {usages.Count} 个系统快捷键,WAIO 一个都没有接管。"
            : $"共 {usages.Count} 个系统快捷键,其中 {taken} 个现在由 WAIO 接管。";

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
        EmptyText.Text = OnlyTaken.IsOn && query.Length == 0 ? "WAIO 的快捷键和系统快捷键都不相同" : "没有匹配的系统快捷键";
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
        text.Children.Add(new TextBlock { Text = Headline(usage), TextWrapping = TextWrapping.Wrap });
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
            ToolTipService.SetToolTip(restore, "停用占用这个组合的 WAIO 快捷键(不删除,可以在\"WAIO 快捷键\"页重新打开)");
            Place(grid, restore, 3);
        }

        var card = SettingsUi.Card("", null, null);
        card.Child = grid;
        return card;
    }

    /// <summary>
    /// 第一行(参考 MAIO):被接管的写"现在触发 WAIO:…",让人一眼看出按下去会发生什么;其余写系统功能。
    /// </summary>
    private string Headline(SystemShortcutUsage usage) => usage.State is SystemShortcutState.TakenOver or SystemShortcutState.PartlyTakenOver
        ? "现在触发 WAIO:" + Owners(usage)
        : usage.Shortcut.Function;

    /// <summary>第二行:被接管的写"系统原本是:…";和 WAIO 冲突的写谁也会触发;其余显示补充说明。</summary>
    private string? Detail(SystemShortcutUsage usage) => usage.State switch
    {
        SystemShortcutState.TakenOver or SystemShortcutState.PartlyTakenOver => "系统原本是:" + usage.Shortcut.Function,
        SystemShortcutState.Clash => $"WAIO 的 {Owners(usage)} 也绑了它:按下时两边都会触发",
        _ => usage.Shortcut.Note,
    };

    private string Owners(SystemShortcutUsage usage) => string.Join("、", usage.Bindings.Select(b =>
        $"「{ShortcutUi.Title(_context, b)}」" + (b.Scope is Waio.Kit.Input.ProcessScope scope ? $"({ShortcutUi.ScopeText(scope)})" : "")));

    private static Border? Badge(SystemShortcutUsage usage)
    {
        (string Text, SettingsUi.Tone Tone, string Tip)? badge = usage.State switch
        {
            SystemShortcutState.TakenOver => ("由 WAIO 接管", SettingsUi.Tone.Success, "WAIO 运行时只触发 WAIO;退出 WAIO 或停用那条快捷键后,系统快捷键恢复工作"),
            SystemShortcutState.PartlyTakenOver => ("部分由 WAIO 接管", SettingsUi.Tone.Neutral, "只在那条快捷键的生效范围内由 WAIO 接管,其他程序里系统功能照常"),
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
