using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Shell.Hosting;

namespace Waio.Shell.Settings;

internal sealed partial class GeneralPage : Page
{
    private ShellContext _context = null!;
    private bool _refreshingSwitch;

    public GeneralPage()
    {
        InitializeComponent();
        Waio.UI.Theming.GlassShape.Card(AutostartCard);
        Waio.UI.Theming.GlassShape.Card(DiagnosticsCard);
        Waio.UI.Theming.GlassShape.Card(UpdateCard);
        Unloaded += (_, _) =>
        {
            if (_context is not null)
            {
                _context.Updates.Changed -= OnUpdateChanged;
            }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (ShellContext)e.Parameter;

        if (_context.IsElevated)
        {
            ElevationBar.Severity = InfoBarSeverity.Success;
            ElevationBar.Title = "已以管理员权限运行";
            ElevationBar.Message = "全局快捷键在所有窗口(包括管理员窗口)里都有效。";
            RelaunchButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ElevationBar.Severity = InfoBarSeverity.Warning;
            ElevationBar.Title = "未以管理员权限运行";
            ElevationBar.Message = "在管理员权限的窗口(任务管理器、管理员终端等)里,快捷键和按键映射不会生效。";
        }

        RefreshDiagnostics();
        InitUpdateCard();
        BuildRemoteDesktopCard();

        // 查询计划任务要起一个子进程,放到后台线程,不卡界面。
        // 上次查到的结果先显示出来并可用,查完有变化再改:否则每次切到这页开关都先变灰再亮,闪一下
        if (s_lastAutostart is { } known)
        {
            SetSwitch(known);
            AutostartSwitch.IsEnabled = true;
        }

        var installed = await Task.Run(_context.IsAutostartInstalled);
        s_lastAutostart = installed;
        if (AutostartSwitch.IsOn != installed)
        {
            SetSwitch(installed);
        }

        AutostartSwitch.IsEnabled = true;
    }

    /// <summary>上次查到的自启状态(进程内共享,页面每次切换都会新建)。</summary>
    private static bool? s_lastAutostart;

    private void SetSwitch(bool on)
    {
        _refreshingSwitch = true;
        AutostartSwitch.IsOn = on;
        _refreshingSwitch = false;
    }

    private async void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_refreshingSwitch)
        {
            return;
        }

        var wanted = AutostartSwitch.IsOn;
        AutostartSwitch.IsEnabled = false;
        var result = await Task.Run(() => _context.SetAutostart(wanted));
        AutostartMessage.Text = result.Message;
        SetSwitch(result.Ok ? wanted : !wanted); // 失败则恢复,界面始终反映真实状态
        s_lastAutostart = AutostartSwitch.IsOn;
        AutostartSwitch.IsEnabled = true;
    }

    private void OnRelaunch(object sender, RoutedEventArgs e)
    {
        if (_context.TryRelaunchElevated())
        {
            Application.Current.Exit();
        }
    }

    private void BuildRemoteDesktopCard() =>
        RemoteDesktopHost.Content = RemoteDesktopCard.Build(_context, XamlRoot, BuildRemoteDesktopCard);

    private void OnRefreshDiagnostics(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private async void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_context.Paths.LogsDirectory);
        await _context.OpenAsync(_context.Paths.LogsDirectory);
    }

    private void RefreshDiagnostics()
    {
        var d = _context.Input.Diagnostics;
        DiagnosticsText.Text =
            $"按键事件 {d.EventsProcessed:N0}(其中真实键盘 {d.PhysicalEvents:N0})\n" +
            $"按键处理耗时:平均 {d.AverageCallbackMicroseconds:N1}µs / 最长 {d.MaxCallbackMicroseconds:N0}µs\n" +
            $"按键状态校正 {d.StateResyncs:N0} 次\n" +
            $"处理出错 {d.Errors:N0} 次";
    }
}
