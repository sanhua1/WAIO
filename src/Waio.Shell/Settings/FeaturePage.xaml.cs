using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Core.Host;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <param name="ShowBindings">切到"快捷键"总表页。</param>
internal sealed record FeaturePageArgs(ShellContext Context, FeatureManifest Manifest, Action ShowBindings);

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
        SummaryText.Text = Manifest.Summary;

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

    // ---- 快捷键(ADR-0011,参考 MAIO 的 FeatureShortcutsSection;和"快捷键"页是同一份数据) ----

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
        foreach (var command in Manifest.Commands)
        {
            var mine = bindings.Where(b => b.Command == command.Id).ToList();
            if (command.Parameters.Any(p => p.Required))
            {
                // 需要参数的命令(打开程序等):一个命令可以有很多条绑定,在编辑器里填参数
                var add = SettingsUi.Button("添加…", () => _ = ShortcutUi.RunAsync(XamlRoot, () =>
                    BindingEditorDialog.EditAsync(Context, BindingEditorDialog.NewBinding(command.Id), isNew: true, XamlRoot)));
                var note = mine.Count == 0 ? "需要填写参数,点\"添加…\"" : $"已有 {mine.Count} 个,在\"快捷键\"页查看和编辑";
                _shortcuts.Children.Add(SettingsUi.Card(command.Title, note, add));
                continue;
            }

            // 无参数的命令:直接点键帽录制;已有多条时每条一个键帽
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

            _shortcuts.Children.Add(SettingsUi.Card(command.Title, command.Id.Value, keys));
        }

        _shortcuts.Children.Add(SettingsUi.Card(
            "全部快捷键", "点击键帽后按下新组合即可修改;单按 Backspace 清除,Esc 取消。生效范围等高级选项在\"快捷键\"页编辑",
            SettingsUi.Button("打开快捷键页", _args.ShowBindings)));
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
