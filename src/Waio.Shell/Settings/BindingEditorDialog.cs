using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Core.Bindings;
using Waio.Kit.Commands;
using Waio.Kit.Input;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 编辑单条绑定(ADR-0011,结构参考 MAIO 的 BindingEditor):
/// 快捷键(录制或手动选键)→ 命令 → 按参数声明生成的表单 → 生效范围 → 启用。下方实时显示检查结果,有错误时不能保存。
/// </summary>
internal sealed class BindingEditorDialog : ContentDialog
{
    private readonly ShellContext _context;
    private readonly CommandBinding _original;
    private readonly Func<nint> _owner;
    private readonly HotkeyRecorder _recorder;
    private readonly ManualKeyPicker _manual;
    private readonly ComboBox _command = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<CommandDescriptor> _commands;
    private readonly ContentControl _parameterHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScopeEditor _scope;
    private readonly ToggleSwitch _enabled = new() { OnContent = "启用", OffContent = "停用" };
    private readonly StackPanel _issues = new() { Spacing = 6 };
    private ParameterForm _parameters = null!;
    private KeyChord? _chord;

    private BindingEditorDialog(ShellContext context, CommandBinding original, bool isNew, XamlRoot root)
    {
        _context = context;
        _original = original;
        _owner = () => Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        _chord = original.Trigger is HotkeyTrigger { Chord: var c } && !string.IsNullOrEmpty(c.Key) ? c : null;

        XamlRoot = root;
        Title = isNew ? "添加快捷键" : "编辑快捷键";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        SecondaryButtonText = isNew ? "" : "删除";
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 640.0;

        _recorder = new HotkeyRecorder(_chord, context.RecordHotkeyAsync, chord => SetChord(chord, fromRecorder: true));
        _manual = new ManualKeyPicker(_chord, context.CustomModifiers, chord => SetChord(chord, fromRecorder: false));
        _scope = new ScopeEditor(original.Scope, Refresh);
        _enabled.IsOn = original.Enabled;
        _enabled.Toggled += (_, _) => Refresh();

        _commands = [.. context.Manifests.SelectMany(m => m.Commands)];
        foreach (var command in _commands)
        {
            _command.Items.Add($"{context.OwnerOf(command.Id)?.DisplayName} · {command.Title}");
        }

        _command.SelectedIndex = Math.Max(0, _commands.FindIndex(c => c.Id == original.Command));
        _command.SelectionChanged += (_, _) => BuildParameters(CommandArguments.Empty);
        BuildParameters(original.Arguments);

        Content = new ScrollViewer
        {
            MaxHeight = 560,
            Padding = new Thickness(0, 0, 16, 0),
            Content = new StackPanel
            {
                Spacing = 14,
                MinWidth = 520,
                Children =
                {
                    Labeled("快捷键", Row(_recorder.View, _manual.Toggle)),
                    _manual.View,
                    Labeled("动作", _command),
                    _parameterHost,
                    _scope.View,
                    _enabled,
                    _issues,
                },
            },
        };

        PrimaryButtonClick += OnSave;
        SecondaryButtonClick += OnDelete;
        Refresh();
    }

    /// <summary>打开编辑器;保存/删除后返回 true。</summary>
    public static async Task<bool> EditAsync(ShellContext context, CommandBinding binding, bool isNew, XamlRoot root)
    {
        var dialog = new BindingEditorDialog(context, binding, isNew, root);
        return await dialog.ShowAsync() != ContentDialogResult.None;
    }

    /// <summary>新建一条绑定的初始值(可预选命令)。</summary>
    public static CommandBinding NewBinding(CommandId command) =>
        new(BindingRules.NewId(), new HotkeyTrigger(default), command, CommandArguments.Empty, BindingScope.Global);

    private CommandBinding Current()
    {
        var command = _commands.Count == 0 ? _original.Command : _commands[Math.Max(0, _command.SelectedIndex)].Id;
        return _original with
        {
            Trigger = new HotkeyTrigger(_chord ?? default),
            Command = command,
            Arguments = _parameters.Read(),
            Scope = _scope.Read(),
            Enabled = _enabled.IsOn,
        };
    }

    private void SetChord(KeyChord? chord, bool fromRecorder)
    {
        _chord = chord;
        if (fromRecorder)
        {
            _manual.SetChord(chord);
        }
        else
        {
            _recorder.SetChord(chord);
        }

        Refresh();
    }

    private void BuildParameters(CommandArguments arguments)
    {
        var descriptor = _commands.Count == 0 ? null : _commands[Math.Max(0, _command.SelectedIndex)];
        _parameters = new ParameterForm(descriptor?.Parameters ?? [], arguments, _owner, Refresh);
        _parameterHost.Content = _parameters.IsEmpty ? null : _parameters.View;
        Refresh();
    }

    /// <summary>重新检查并显示问题;有错误时禁用"保存"。</summary>
    private void Refresh()
    {
        if (_parameters is null)
        {
            return; // 构造过程中
        }

        var issues = _context.CheckBinding(Current());
        _issues.Children.Clear();
        foreach (var issue in issues)
        {
            _issues.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = issue.Level == BindingIssueLevel.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                Message = issue.Message,
            });
        }

        IsPrimaryButtonEnabled = issues.All(i => i.Level != BindingIssueLevel.Error);
    }

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            Refresh(); // 保存前以最新状态再查一次
            if (!IsPrimaryButtonEnabled)
            {
                args.Cancel = true;
                return;
            }

            await _context.SaveBindingAsync(Current());
        }
        catch (IOException ex)
        {
            args.Cancel = true;
            _issues.Children.Insert(0, new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Error, Message = $"保存失败:{ex.Message}" });
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnDelete(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await _context.DeleteBindingAsync(_original.Id);
        }
        catch (IOException ex)
        {
            args.Cancel = true;
            _issues.Children.Insert(0, new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Error, Message = $"删除失败:{ex.Message}" });
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static StackPanel Labeled(string label, UIElement content) =>
        new() { Spacing = 6, Children = { new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] }, content } };

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var item in items)
        {
            row.Children.Add(item);
        }

        return row;
    }
}
