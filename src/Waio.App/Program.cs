using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Waio.Shell.Hosting;

namespace Waio.App;

/// <summary>入口:先做单实例/提权决策,再启动 WinUI。</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var session = StartupSession.Begin(args);
        if (!session.ShouldRun)
        {
            return session.ExitCode;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("domain", (Exception)e.ExceptionObject);

        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(session);
        });
        return 0;
    }
}
