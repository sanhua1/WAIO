using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Waio.Core.Storage;
using Waio.Shell.Hosting;
using Waio.UI;

namespace Waio.Shell.Settings;

/// <summary>
/// 通用页的"远程桌面"卡片(ADR-0033):开关 + 程序名单(移除、从运行中的程序添加、手动输入、恢复默认)。
/// 每次修改后由 <paramref name="rebuild"/> 整体重建。
/// </summary>
internal static class RemoteDesktopCard
{
    /// <summary>默认名单里程序的中文说明(进程名不区分大小写)。</summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GameViewer"] = "网易 UU 远程",
        ["ToDesk"] = "ToDesk",
        ["SunloginClient"] = "向日葵",
        ["mstsc"] = "Windows 远程桌面连接",
        ["msrdc"] = "Windows App / 远程桌面客户端",
        ["parsecd"] = "Parsec",
        ["TeamViewer"] = "TeamViewer",
        ["AnyDesk"] = "AnyDesk",
        ["rustdesk"] = "RustDesk",
        ["vncviewer"] = "VNC Viewer",
    };

    public static StackPanel Build(ShellContext context, XamlRoot? root, Action rebuild)
    {
        var settings = context.Settings.RemoteDesktop;
        var apps = RemoteDesktopSettings.Normalize(settings.Apps);

        void Save(bool enabled, IEnumerable<string> list) =>
            _ = ShortcutUi.RunAsync(root, async () =>
            {
                await context.SetRemoteDesktopAsync(enabled, list);
                rebuild();
            });

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(SettingsUi.Card(
            "远程桌面在前台时,把按键让给远程电脑",
            SettingsUi.Lines(
                "名单里的程序在前台时,WAIO 的快捷键、按键映射、Super Caps、自定义修饰键都不生效",
                "按键原样交给远程软件转发到远程电脑",
                "Win 键组合能不能传过去,取决于远程软件自己的设置(例如\"发送 Windows 组合键\")"),
            SettingsUi.Toggle(settings.Enabled, on => Save(on, apps))));

        if (!settings.Enabled)
        {
            return panel;
        }

        var list = new StackPanel { Spacing = 2 };
        if (apps.Count == 0)
        {
            list.Children.Add(SettingsUi.Hint("名单是空的"));
        }

        foreach (var app in apps)
        {
            list.Children.Add(Row(app, () => Save(true, apps.Where(a => !string.Equals(a, app, StringComparison.OrdinalIgnoreCase)))));
        }

        var input = new TextBox { PlaceholderText = "进程名,如 GameViewer.exe", Width = 220 };
        void AddTyped()
        {
            if (RemoteDesktopSettings.NormalizeOne(input.Text) is { } name)
            {
                Save(true, [.. apps, name]);
            }
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                AddTyped();
            }
        };

        var running = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        running.Opening += (_, _) => FillRunning(running, apps, name => Save(true, [.. apps, name]));

        var restore = SettingsUi.Button("恢复默认名单", () => Save(true, RemoteDesktopSettings.DefaultApps));
        restore.IsEnabled = !apps.SequenceEqual(RemoteDesktopSettings.DefaultApps, StringComparer.OrdinalIgnoreCase);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new DropDownButton { Content = "从运行中的程序添加", Flyout = running },
                input,
                SettingsUi.Button("添加", AddTyped),
                restore,
            },
        };

        var body = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "算作远程桌面的程序" },
                new TextBlock
                {
                    Text = "按进程名匹配(不区分大小写)。不确定名字时,先打开远程软件的控制窗口,再从\"运行中的程序\"里选;"
                        + "日志里\"远程桌面在前台\"一行能确认是否生效。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
                list,
                actions,
            },
        };
        panel.Children.Add(Waio.UI.Theming.GlassShape.Card(new Border
        {
            Padding = new Thickness(18, 12, 18, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["WaioCardCornerRadius"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = body,
        }));
        return panel;
    }

    private static Grid Row(string app, Action remove)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 32 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = KnownNames.TryGetValue(app, out var known) ? known : app });
        text.Children.Add(SettingsUi.Hint(app + ".exe"));
        row.Children.Add(text);

        var button = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Padding = new Thickness(8, 4, 8, 4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, "从名单中移除");
        button.Click += (_, _) => remove();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    /// <summary>列出有窗口、还不在名单里的进程(打开菜单时才枚举)。</summary>
    private static void FillRunning(MenuFlyout menu, IReadOnlyList<string> existing, Action<string> add)
    {
        menu.Items.Clear();
        var self = Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != self && process.MainWindowHandle != 0 && !existing.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch (InvalidOperationException)
                {
                    // 进程刚好退出了
                }
            }
        }

        foreach (var name in names)
        {
            var item = new MenuFlyoutItem { Text = KnownNames.TryGetValue(name, out var known) ? $"{name}({known})" : name };
            item.Click += (_, _) => add(name);
            menu.Items.Add(item);
        }

        if (names.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "没有找到", IsEnabled = false });
        }
    }
}
