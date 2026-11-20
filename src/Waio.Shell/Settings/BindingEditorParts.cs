using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Core.Input;
using Waio.Kit.Input;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 手动选键:修饰键复选框 +(有声明时)自定义修饰键下拉 + 主键下拉。用于键盘上没有的键(F13–F24 等)、或输入引擎没运行时无法录制的情况。
/// </summary>
internal sealed class ManualKeyPicker
{
    private readonly Action<KeyChord?> _changed;
    private readonly CheckBox _ctrl = new() { Content = "Ctrl", MinWidth = 0 };
    private readonly CheckBox _shift = new() { Content = "Shift", MinWidth = 0 };
    private readonly CheckBox _alt = new() { Content = "Alt", MinWidth = 0 };
    private readonly CheckBox _win = new() { Content = "Win", MinWidth = 0 };
    private readonly ComboBox _key = new() { PlaceholderText = "主键", MinWidth = 140, MaxDropDownHeight = 360 };
    private readonly ComboBox _hold = new() { MinWidth = 120, PlaceholderText = "自定义修饰键" };
    private readonly IReadOnlyList<string> _holdKeys;
    private bool _syncing;

    /// <param name="customModifiers">已声明的自定义修饰键;为空时不显示该下拉。</param>
    public ManualKeyPicker(KeyChord? chord, IReadOnlyList<string> customModifiers, Action<KeyChord?> changed)
    {
        _changed = changed;
        _holdKeys = customModifiers;
        _hold.Items.Add("无自定义修饰键");
        foreach (var key in customModifiers)
        {
            _hold.Items.Add("按住 " + KeyChordText.Key(key));
        }

        foreach (var key in KeyNames.PrimaryKeys)
        {
            _key.Items.Add(KeyChordText.Key(key));
        }

        View = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children = { _ctrl, _shift, _alt, _win },
        };
        if (customModifiers.Count > 0)
        {
            View.Children.Add(_hold);
        }

        View.Children.Add(_key);
        Toggle = new HyperlinkButton { Content = "手动选择按键", VerticalAlignment = VerticalAlignment.Center };
        Toggle.Click += (_, _) => View.Visibility = View.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        foreach (var box in new[] { _ctrl, _shift, _alt, _win })
        {
            box.Click += (_, _) => Emit();
        }

        _key.SelectionChanged += (_, _) => Emit();
        _hold.SelectionChanged += (_, _) => Emit();
        SetChord(chord);
    }

    public StackPanel View { get; }

    /// <summary>"手动选择按键"链接,点了展开/收起 <see cref="View"/>。</summary>
    public HyperlinkButton Toggle { get; }

    /// <summary>同步显示(录制器录到新键时),不触发回调。</summary>
    public void SetChord(KeyChord? chord)
    {
        _syncing = true;
        try
        {
            var modifiers = chord?.Modifiers ?? KeyModifiers.None;
            _ctrl.IsChecked = modifiers.HasFlag(KeyModifiers.Ctrl);
            _shift.IsChecked = modifiers.HasFlag(KeyModifiers.Shift);
            _alt.IsChecked = modifiers.HasFlag(KeyModifiers.Alt);
            _win.IsChecked = modifiers.HasFlag(KeyModifiers.Win);
            _key.SelectedIndex = chord is { Key: { Length: > 0 } key } ? IndexOf(key) : -1;
            _hold.SelectedIndex = chord?.Hold is { } hold ? Math.Max(0, IndexOfHold(hold) + 1) : 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Emit()
    {
        if (_syncing || _key.SelectedIndex < 0)
        {
            return;
        }

        var modifiers = KeyModifiers.None;
        if (_ctrl.IsChecked == true) { modifiers |= KeyModifiers.Ctrl; }
        if (_shift.IsChecked == true) { modifiers |= KeyModifiers.Shift; }
        if (_alt.IsChecked == true) { modifiers |= KeyModifiers.Alt; }
        if (_win.IsChecked == true) { modifiers |= KeyModifiers.Win; }
        var hold = _hold.SelectedIndex > 0 ? _holdKeys[_hold.SelectedIndex - 1] : null;
        _changed(new KeyChord(modifiers, KeyNames.PrimaryKeys[_key.SelectedIndex], hold));
    }

    private int IndexOfHold(string key)
    {
        for (var i = 0; i < _holdKeys.Count; i++)
        {
            if (_holdKeys[i] == KeyNames.Canonicalize(key))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOf(string key)
    {
        var canonical = KeyNames.Canonicalize(key);
        for (var i = 0; i < KeyNames.PrimaryKeys.Count; i++)
        {
            if (KeyNames.PrimaryKeys[i] == canonical)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>生效范围:所有程序 / 仅在这些程序中 / 除了这些程序。</summary>
internal sealed class ScopeEditor
{
    private readonly ComboBox _mode = new() { Items = { "所有程序", "仅在以下程序中", "除以下程序外" }, MinWidth = 180 };
    private readonly TextBox _names = new()
    {
        PlaceholderText = "进程名,用逗号分隔,如 code, notepad, WindowsTerminal",
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public ScopeEditor(BindingScope scope, Action changed)
    {
        if (scope is ProcessScope process)
        {
            _mode.SelectedIndex = process.Exclude ? 2 : 1;
            _names.Text = string.Join(", ", process.ProcessNames);
        }
        else
        {
            _mode.SelectedIndex = 0;
        }

        _mode.SelectionChanged += (_, _) =>
        {
            _names.Visibility = _mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            changed();
        };
        _names.TextChanged += (_, _) => changed();
        _names.Visibility = _mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;

        View = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "生效范围", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                _mode,
                _names,
            },
        };
    }

    public StackPanel View { get; }

    /// <summary>名单为空时按"所有程序"处理(和 MAIO 一致)。进程名去掉 .exe,和匹配时的写法一致。</summary>
    public BindingScope Read()
    {
        var names = _names.Text
            .Split([',', '\uFF0C', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _mode.SelectedIndex == 0 || names.Count == 0
            ? BindingScope.Global
            : new ProcessScope(names, Exclude: _mode.SelectedIndex == 2);
    }
}
