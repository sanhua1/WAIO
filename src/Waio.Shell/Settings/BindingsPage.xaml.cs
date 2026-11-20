using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Core.Bindings;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// "WAIO 快捷键"页:所有绑定的总表(ADR-0011,参考 MAIO 的 ShortcutsPane)。
/// 按功能分组(<see cref="BindingGroups"/>),组头是"图标 + 名称 + 条数";每行见 <see cref="BindingRow"/>。
/// 功能自己的快捷键也可以在各功能页里直接设置,两边是同一份数据。
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

        // 分组同 MAIO:各功能(顺序同设置窗口左栏)→ 未知命令 → 带参数的命令各一组(打开程序、打开网页)
        foreach (var group in BindingGroups.Make(bindings, _context.Manifests))
        {
            Groups.Children.Add(ShortcutUi.GroupHeader(group.Title, group.Glyph, group.Bindings.Count, group.IsFeature));
            foreach (var binding in group.Bindings)
            {
                Groups.Children.Add(BindingRow.Build(_context, binding, group.Glyph, XamlRoot));
            }
        }

        EmptyState.Visibility = bindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = query.Length > 0 ? "没有匹配的快捷键" : "还没有快捷键,点右上角\"添加快捷键\"";
    }

    /// <summary>搜索:组合、标题、说明,以及所属功能的名字(功能名不在行里显示,在组头上)。</summary>
    private bool Matches(CommandBinding binding, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        var chord = binding.Trigger is HotkeyTrigger { Chord: var c } ? KeyChordText.Display(c) + " " + c : "";
        var owner = _context.OwnerOf(binding.Command)?.DisplayName;
        return $"{chord} {ShortcutUi.Title(_context, binding)} {ShortcutUi.Detail(_context, binding)} {owner} {binding.Command}"
            .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

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
}
