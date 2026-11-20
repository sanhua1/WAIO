using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Kit.Commands;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 按命令的 <see cref="ParameterSpec"/> 自动生成参数表单(ARCHITECTURE §4.2,参考 MAIO 的 ParameterForm),
/// 并在 <see cref="CommandArguments"/> 与控件之间读写。新增命令不需要改界面代码。
/// </summary>
internal sealed class ParameterForm
{
    /// <summary>提权运行开关(ADR-0006):界面上必须醒目标注风险。</summary>
    private const string RunElevated = "runElevated";

    private readonly List<(ParameterSpec Spec, Func<JsonElement?> Read)> _fields = [];
    private readonly Action _changed;

    /// <param name="owner">文件选择对话框的父窗口。</param>
    /// <param name="changed">任何字段被修改时回调(编辑器据此重新检查)。</param>
    public ParameterForm(IReadOnlyList<ParameterSpec> parameters, CommandArguments current, Func<nint> owner, Action changed)
    {
        _changed = changed;
        View = new StackPanel { Spacing = 12 };
        foreach (var spec in parameters)
        {
            current.Values.TryGetValue(spec.Name, out var value);
            View.Children.Add(Build(spec, value, owner));
        }
    }

    public StackPanel View { get; }

    public bool IsEmpty => _fields.Count == 0;

    /// <summary>当前填写的参数(空字符串保留为空字符串,由 BindingRules 统一清理和校验)。</summary>
    public CommandArguments Read()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (spec, read) in _fields)
        {
            if (read() is { } value)
            {
                values[spec.Name] = value;
            }
        }

        return values.Count == 0 ? CommandArguments.Empty : new CommandArguments(values);
    }

    private FrameworkElement Build(ParameterSpec spec, JsonElement value, Func<nint> owner)
    {
        var header = spec.Required ? spec.Title + " *" : spec.Title;
        switch (spec.Kind)
        {
            case ParameterKind.Bool:
            {
                var toggle = new ToggleSwitch { Header = header, IsOn = value.ValueKind == JsonValueKind.True };
                toggle.Toggled += (_, _) => _changed();
                _fields.Add((spec, () => toggle.IsOn ? JsonSerializer.SerializeToElement(true) : null));
                if (spec.Name != RunElevated)
                {
                    return toggle;
                }

                var warning = new TextBlock
                {
                    Text = "危险:打开后程序以管理员权限运行,其他程序如果改动了快捷键配置,也可能借此获得管理员权限。只对你信任的程序打开。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                };
                warning.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                return new StackPanel { Spacing = 2, Children = { toggle, warning } };
            }

            case ParameterKind.Number:
            {
                var box = new NumberBox
                {
                    Header = header,
                    Value = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                };
                box.ValueChanged += (_, _) => _changed();
                _fields.Add((spec, () => double.IsNaN(box.Value) ? null : JsonSerializer.SerializeToElement(box.Value)));
                return box;
            }

            default:
            {
                var box = new TextBox
                {
                    Header = header,
                    Text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "",
                    PlaceholderText = Placeholder(spec.Kind),
                };
                box.TextChanged += (_, _) => _changed();
                _fields.Add((spec, () => JsonSerializer.SerializeToElement((box.Text ?? "").Trim())));
                if (spec.Kind != ParameterKind.FilePath)
                {
                    return box;
                }

                var browse = SettingsUi.Button("浏览…", () =>
                {
                    if (FileDialogs.PickFile(owner(), "选择" + spec.Title, (box.Text ?? "").Trim()) is { } path)
                    {
                        box.Text = path;
                    }
                });
                browse.VerticalAlignment = VerticalAlignment.Bottom;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(browse, 1);
                row.Children.Add(box);
                row.Children.Add(browse);
                return row;
            }
        }
    }

    private static string Placeholder(ParameterKind kind) => kind switch
    {
        ParameterKind.FilePath => "完整路径(可用 %ProgramFiles% 等环境变量),或 notepad 这样的命令名",
        ParameterKind.Url => "https://…",
        _ => "",
    };
}
