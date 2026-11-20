using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Waio.Feature.Translate.Domain;
using Waio.UI;

namespace Waio.Feature.Translate.Views;

/// <summary>
/// 翻译源列表(MAIO <c>ServiceListView</c> / <c>ServiceEditorView</c>):启用开关、排序、展开编辑、添加和删除、一键测试。
/// 结果卡片按这里的顺序排列。配置表单按 <see cref="ProviderSpec"/> 自动生成;密钥写入密钥存储(停止输入 0.5 秒后保存)。
/// </summary>
internal sealed partial class TranslateSettingsView
{
    private const string ServicesInfo =
        "打开的翻译源会同时翻译,结果按列表顺序显示。免费源(Google、必应、腾讯交互翻译)使用网页接口,不需要 Key,但对方改版时可能失效。"
        + "需要 Key 的源请点\"申请 Key\"获取;Key 只用当前 Windows 用户能解开的方式加密保存在本机,不会写进配置文件。";

    private string? _expanded;

    private IEnumerable<UIElement> ServiceSection()
    {
        yield return SettingsUi.Section("翻译源(按顺序显示结果)");
        yield return SettingsUi.Card("翻译源", ServicesInfo, AddMenu());
        foreach (var service in _value.Services)
        {
            yield return ServiceRow(service);
        }
    }

    private DropDownButton AddMenu()
    {
        var menu = new MenuFlyout();
        var llm = new MenuFlyoutSubItem { Text = "大模型(OpenAI 兼容)" };
        foreach (var preset in LlmPreset.All)
        {
            llm.Items.Add(MenuItem(preset.Name, () => Add(preset.MakeConfig(ServiceConfig.NewId()))));
        }

        llm.Items.Add(new MenuFlyoutSeparator());
        llm.Items.Add(MenuItem("自定义…", () => Add(new ServiceConfig
        {
            Id = ServiceConfig.NewId(), Kind = ProviderKind.OpenAICompatible, Name = "自定义大模型",
        })));
        menu.Items.Add(llm);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var kind in ProviderCatalog.All.Where(k => k != ProviderKind.OpenAICompatible))
        {
            var item = MenuItem(kind.Spec().DisplayName, () => Add(ServiceConfig.Make(kind, ServiceConfig.NewId())));
            item.IsEnabled = !_value.Services.Exists(s => s.Kind == kind);
            menu.Items.Add(item);
        }

        return new DropDownButton { Content = "添加翻译源", Flyout = menu };
    }

    private void Add(ServiceConfig config)
    {
        _value.Services.Add(config);
        _expanded = config.Id;
        SaveAndRebuild();
    }

    private Expander ServiceRow(ServiceConfig service)
    {
        var name = new TextBlock { Text = service.Name, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new ToggleSwitch { IsOn = service.IsEnabled, OnContent = "", OffContent = "", MinWidth = 0, Width = 44 };
        toggle.Toggled += (_, _) => Save(_ => service.IsEnabled = toggle.IsOn);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(toggle);
        header.Children.Add(Mark(service.Spec.Mark));
        header.Children.Add(name);
        header.Children.Add(CostBadge(service.Spec.Cost));

        var expander = new Expander
        {
            Header = header, IsExpanded = _expanded == service.Id,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = Editor(service, name),
        };
        expander.Expanding += (_, _) => _expanded = service.Id;
        return expander;
    }

    /// <summary>单个翻译源的配置表单(MAIO ServiceEditorView)。</summary>
    private StackPanel Editor(ServiceConfig service, TextBlock headerName)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Wrap(SettingsUi.Hint(service.Spec.Summary)));

        var nameBox = new TextBox { Header = "显示名称", Text = service.Name };
        nameBox.TextChanged += (_, _) =>
        {
            headerName.Text = nameBox.Text;
            Save(_ => service.Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : service.Spec.DisplayName);
        };
        panel.Children.Add(nameBox);
        foreach (var field in service.Spec.FieldList)
        {
            panel.Children.Add(FieldEditor(service, field));
        }

        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var testButton = new Button { Content = "测试" };
        testButton.Click += async (_, _) => await TestAsync(service, testButton, testResult);
        testRow.Children.Add(testButton);
        if (SignupUrl(service) is { } signup)
        {
            var link = new HyperlinkButton { Content = "申请 Key" };
            link.Click += (_, _) => _ = OpenUrlAsync(signup);
            testRow.Children.Add(link);
        }

        panel.Children.Add(testRow);
        panel.Children.Add(testResult);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(SettingsUi.Button("上移", () => Move(service, -1)));
        actions.Children.Add(SettingsUi.Button("下移", () => Move(service, 1)));
        var remove = SettingsUi.Button("删除这个翻译源", () => Remove(service));
        remove.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        actions.Children.Add(remove);
        panel.Children.Add(actions);
        return panel;
    }

    private UIElement FieldEditor(ServiceConfig service, ProviderField field)
    {
        switch (field.Kind)
        {
            case FieldKind.Secret:
                var box = new PasswordBox { Header = field.Title, PlaceholderText = "加密保存在本机", Password = _context.Secrets.Get(service.SecretKey(field.Key)) ?? "" };
                var error = new TextBlock { FontSize = 12, Visibility = Visibility.Collapsed, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
                var timer = box.DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(500);
                timer.IsRepeating = false;
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        _context.Secrets.Set(service.SecretKey(field.Key), box.Password.Trim());
                        error.Visibility = Visibility.Collapsed;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        error.Text = ex.Message;
                        error.Visibility = Visibility.Visible;
                    }
                };
                box.PasswordChanged += (_, _) =>
                {
                    timer.Stop();
                    timer.Start(); // 停止输入 0.5 秒后保存(MAIO)
                };
                var secret = new StackPanel { Spacing = 2 };
                secret.Children.Add(box);
                secret.Children.Add(error);
                return secret;

            case FieldKind.Multiline:
                var editor = new TextBox
                {
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 120,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
                    Text = service.Options.GetValueOrDefault(field.Key) ?? field.DefaultValue, // 没填时显示默认值,方便在默认值基础上修改
                };
                editor.TextChanged += (_, _) => Save(_ => SetOption(service, field.Key, editor.Text == field.DefaultValue ? null : editor.Text));
                var title = new Grid();
                title.Children.Add(new TextBlock { Text = field.Title, VerticalAlignment = VerticalAlignment.Center });
                var reset = SettingsUi.Button("恢复默认", () => editor.Text = field.DefaultValue);
                reset.HorizontalAlignment = HorizontalAlignment.Right;
                title.Children.Add(reset);
                var multiline = new StackPanel { Spacing = 4 };
                multiline.Children.Add(title);
                multiline.Children.Add(editor);
                return multiline;

            default:
                var text = new TextBox
                {
                    Header = field.Title, Text = service.Options.GetValueOrDefault(field.Key) ?? "",
                    PlaceholderText = field.Placeholder.Length == 0 ? field.DefaultValue : field.Placeholder,
                };
                text.TextChanged += (_, _) => Save(_ => SetOption(service, field.Key, text.Text.Length == 0 ? null : text.Text));
                return text;
        }
    }

    private static void SetOption(ServiceConfig service, string key, string? value)
    {
        if (value is null)
        {
            service.Options.Remove(key);
        }
        else
        {
            service.Options[key] = value;
        }
    }

    /// <summary>用 "hello" 实际翻译一次(MAIO ServiceEditorView.test)。</summary>
    private async Task TestAsync(ServiceConfig service, Button button, TextBlock result)
    {
        button.IsEnabled = false;
        result.Visibility = Visibility.Visible;
        result.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        result.Text = "测试中…";
        var request = new TranslationRequest("hello", Language.English, Language.SimplifiedChinese, Language.English);
        try
        {
            var provider = _factory.Make(service);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var translated = await Task.Run(() => provider.TranslateAsync(request, _ => { }, timeout.Token));
            result.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            result.Text = $"✓ 可以使用:hello → {translated.Text}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            result.Text = "✗ " + (ex is TranslationException ? ex.Message : ex is OperationCanceledException ? "请求超时" : ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string? SignupUrl(ServiceConfig service) => service.Kind == ProviderKind.OpenAICompatible
        ? LlmPreset.All.FirstOrDefault(p => p.BaseUrl == service.Option("baseURL"))?.SignupUrl
        : service.Spec.SignupUrl;

    private void Move(ServiceConfig service, int offset)
    {
        var index = _value.Services.IndexOf(service);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _value.Services.Count)
        {
            return;
        }

        (_value.Services[index], _value.Services[target]) = (_value.Services[target], _value.Services[index]);
        _expanded = service.Id;
        SaveAndRebuild();
    }

    /// <summary>删除:连同它的密钥一起删掉(MAIO)。</summary>
    private void Remove(ServiceConfig service)
    {
        foreach (var field in service.Spec.FieldList.Where(f => f.Kind == FieldKind.Secret))
        {
            try
            {
                _context.Secrets.Set(service.SecretKey(field.Key), null);
            }
            catch (IOException)
            {
                // 密钥文件写不进去:不影响删除翻译源本身
            }
        }

        _value.Services.Remove(service);
        SaveAndRebuild();
    }

    private void SaveAndRebuild()
    {
        Save(_ => { });
        Build();
    }

    private static MenuFlyoutItem MenuItem(string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>翻译源的小图标:字母徽标(MAIO 用 SF Symbols 的字母圆圈)。</summary>
    private static Border Mark(string mark) => new()
    {
        Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
        Child = new TextBlock
        {
            Text = mark, FontSize = mark.Length > 1 ? 9 : 11, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static Border CostBadge(ProviderCost cost) => cost switch
    {
        ProviderCost.Free => SettingsUi.Badge("免费", SettingsUi.Tone.Success),
        ProviderCost.FreeQuota => SettingsUi.Badge("需 Key · 有免费额度", SettingsUi.Tone.Neutral),
        _ => SettingsUi.Badge("需 Key", SettingsUi.Tone.Caution),
    };

    private static TextBlock Wrap(TextBlock block)
    {
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }
}
