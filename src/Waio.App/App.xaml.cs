using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Waio.Shell.Hosting;

namespace Waio.App;

public partial class App : Application
{
    private readonly StartupSession _session;
    private ShellHost? _host;

    public App(StartupSession session)
    {
        _session = session;
        InitializeComponent();
        // 托盘常驻应用:关掉最后一个窗口(如设置窗口)不能让程序退出,只有托盘"退出"才退出
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => CrashLog.Write("app", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = ShellHost.Create(FeatureCatalog.All, FeatureCatalog.SettingsPages, DispatcherQueue.GetForCurrentThread());
            _host.ExitRequested += OnExitRequested;
            _session.ActivationRequested += _host.ShowSettings; // 来自后台线程,ShowSettings 内部会切回 UI 线程

            await _host.StartAsync(_session.StartInputEngine);
            if (_session.ShowSettingsOnStart)
            {
                _host.ShowSettings();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("startup", ex);
            Exit();
        }
    }

    private async void OnExitRequested()
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            await host.DisposeAsync();
        }

        Exit();
    }
}

