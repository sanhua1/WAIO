using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Core.Bindings;
using Waio.Core.Host;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

internal sealed record FeaturePageArgs(ShellContext Context, FeatureManifest Manifest);

/// <summary>
/// 一个功能的设置页(结构参考 MAIO 的 FeaturePane,ADR-0010):
/// 启用开关与状态 → 这个功能的快捷键 → 功能自带的设置界面(没有则提供"打开设置文件")。
/// </summary>
internal sealed partial class FeaturePage : Page
{
    private FeaturePageArgs _args = null!;
    private Border _stateCard = null!;
    private ToggleSwitch _toggle = null!;
    private StackPanel? _shortcuts;

    public FeaturePage()
    {
        InitializeComponent();
        Waio.UI.Theming.GlassShape.Card(IconBox);
    }

    private ShellContext Context => _args.Context;

    private FeatureManifest Manifest => _args.Manifest;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (FeaturePageArgs)e.Parameter;
        Icon.Glyph = Manifest.IconGlyph;
        TitleText.Text = Manifest.DisplayName;
        ShowSummary();

        Body.Children.Clear();
        Body.Children.Add(EnableCard());
        AddShortcuts();
        AddSettings();
        Context.Features.StatusChanged += OnStatusChanged;
        Context.BindingsChanged += OnBindingsChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Context.Features.StatusChanged -= OnStatusChanged;
        Context.BindingsChanged -= OnBindingsChanged;
    }

    /// <summary>页头说明(参考 MAIO):第一行一句话说用途,后面每行一个要点,逐条显示。</summary>
    private void ShowSummary()
    {
        var lines = Manifest.Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SummaryText.Text = lines.FirstOrDefault() ?? "";
        SummaryNotes.Children.Clear();
        if (lines.Length > 1)
        {
            var notes = SettingsUi.Notes(lines[1..]);
            notes.Margin = new Thickness(0, 4, 0, 0);
            SummaryNotes.Children.Add(notes);
        }
    }

    // ---- 启用 ----

    private Border EnableCard()
    {
        _toggle = SettingsUi.Toggle(Context.IsFeatureEnabled(Manifest), enabled => _ = SetEnabledAsync(enabled));
        _stateCard = SettingsUi.Card("启用", DescribeState(Context.Features.GetStatus(Manifest.Id)), _toggle);
        return _stateCard;
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        try
        {
            await Context.SetFeatureEnabledAsync(Manifest.Id, enabled);
        }
        catch (IOException ex)
        {
            _toggle.IsOn = !enabled; // 保存失败:开关恢复原状,不让界面和实际状态不一致
            SetStateText($"保存设置失败:{ex.Message}");
        }
    }

    // 状态变化来自功能宿主的任意线程,切回 UI 线程再改界面
    private void OnStatusChanged(FeatureStatus status)
    {
        if (status.Id == Manifest.Id)
        {
            Context.Ui.TryEnqueue(() => SetStateText(DescribeState(status)));
        }
    }

    private void SetStateText(string text)
    {
        if (_stateCard.FindName("Description") is TextBlock description)
        {
            description.Text = text;
        }
    }

    private static string DescribeState(FeatureStatus status) => status.State switch
    {
        FeatureState.Running => "运行中。开关立即生效,不需要重启",
        FeatureState.Disabled => "已停用。打开后立即生效",
        FeatureState.NeedsAttention => $"需要处理:{status.Message}",
        FeatureState.Failed => $"出错,已隔离(不影响其他功能):{status.Message}",
        _ => "",
    };

    // ---- 快捷键(ADR-0011,参考 MAIO 的 FeatureShortcutsSection;和"WAIO 快捷键"页是同一份数据) ----

    private void AddShortcuts()
    {
        if (Manifest.Commands.Count == 0)
        {
            return;
        }

        Body.Children.Add(SettingsUi.Section("快捷键"));
        _shortcuts = new StackPanel { Spacing = 4 };
        Body.Children.Add(_shortcuts);
        RefreshShortcuts();
    }

    private void RefreshShortcuts()
    {
        if (_shortcuts is null)
        {
            return;
        }

        _shortcuts.Children.Clear();
        var bindings = Context.Bindings.Bindings;
        var parameterized = Manifest.Commands.Where(BindingGroups.NeedsArguments).ToList();
        foreach (var command in Manifest.Commands.Where(c => !BindingGroups.NeedsArguments(c)))
        {
            // 无参数的命令:一个命令一行,直接点键帽录制;已有多条时每条一个键帽
            var mine = bindings.Where(b => b.Command == command.Id).ToList();
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var binding in mine)
            {
                if (ShortcutUi.Status(Context, binding) is { } status)
                {
                    keys.Children.Add(status);
                }

                keys.Children.Add(ShortcutUi.Recorder(Context, binding, command.Id, XamlRoot).View);
            }

            if (mine.Count == 0)
            {
                keys.Children.Add(ShortcutUi.Recorder(Context, null, command.Id, XamlRoot).View);
            }

            var scopes = mine.Select(b => b.Scope).OfType<ProcessScope>().Select(ShortcutUi.ScopeText).ToList();
            var card = SettingsUi.Card(command.Title, scopes.Count > 0 ? string.Join(" · ", scopes) : null, keys);
            if (mine.Count == 1)
            {
                card.DoubleTapped += (_, e) =>
                {
                    e.Handled = true;
                    _ = BindingRow.EditAsync(Context, mine[0], XamlRoot);
                };
            }

            _shortcuts.Children.Add(card);
        }

        // 需要参数的命令(打开程序、打开网页):已有的绑定逐条列出,双击或右侧菜单编辑;"添加…"在这里直接新建(同 MAIO)
        foreach (var binding in bindings.Where(b => parameterized.Any(c => c.Id == b.Command))
                     .OrderBy(b => parameterized.FindIndex(c => c.Id == b.Command)))
        {
            _shortcuts.Children.Add(BindingRow.Build(Context, binding, Manifest.IconGlyph, XamlRoot));
        }

        if (parameterized.Count > 0)
        {
            var adds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var command in parameterized)
            {
                adds.Children.Add(SettingsUi.Button($"添加:{command.Title}…", () => _ = ShortcutUi.RunAsync(XamlRoot, () =>
                    BindingEditorDialog.EditAsync(Context, BindingEditorDialog.NewBinding(command.Id), isNew: true, XamlRoot))));
            }

            _shortcuts.Children.Add(adds);
        }

        _shortcuts.Children.Add(SettingsUi.Notes(ShortcutNotes(parameterized.Count > 0)));
    }

    private string[] ShortcutNotes(bool hasParameterized)
    {
        if (!Context.IsInputRunning)
        {
            return ["按键钩子没有运行,快捷键暂不生效。"];
        }

        var notes = new List<string>
        {
            "点键帽后按下新的组合;录制时单按 Backspace 删除这个快捷键,Esc 取消。",
            hasParameterized ? "双击一行或用右侧菜单编辑参数和生效范围。" : "双击一行可以设置生效范围(只在某些程序里生效)。",
            "和「WAIO 快捷键」页是同一份设置,一边改了另一边立即同步。",
        };
        return [.. notes];
    }

    private void OnBindingsChanged() => DispatcherQueue.TryEnqueue(RefreshShortcuts);
    // ---- 功能自带的设置 ----

    private void AddSettings()
    {
        if (Context.CreateFeatureSettings(Manifest.Id) is { } view)
        {
            Body.Children.Add(view);
            return;
        }

        var file = Context.FeatureSettingsFile(Manifest.Id);
        Body.Children.Add(SettingsUi.Section("设置"));
        Body.Children.Add(SettingsUi.Card(
            "这个功能还没有图形化设置",
            $"可以直接编辑设置文件,保存后立即生效:{file}",
            SettingsUi.Button("打开设置文件夹", () => _ = Context.OpenAsync(Path.GetDirectoryName(file)!))));
    }
}
