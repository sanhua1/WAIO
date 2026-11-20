using Microsoft.Extensions.Logging.Abstractions;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Panel;
using Waio.Kit.Commands;
using Waio.Testing;
using Waio.UI;

namespace Waio.Feature.Launcher.Tests;

/// <summary>底栏齿轮:直接打开 WAIO 设置(ADR-0025)。</summary>
public class SettingsButtonTests
{
    [Fact]
    public async Task 点齿轮_先关面板_再打开设置()
    {
        var log = new List<string>();
        var view = new LoggingView(log);
        var presenter = NewPresenter(view, () =>
        {
            log.Add("settings");
            return Task.CompletedTask;
        }, new RecordingOverlay());
        presenter.Show(LauncherSource.All);

        await presenter.OpenSettingsAsync();

        Assert.Equal(["show", "hide", "settings"], log);
    }

    [Fact]
    public async Task 设置打不开_提示一下_不抛()
    {
        var overlay = new RecordingOverlay();
        var presenter = NewPresenter(new LoggingView([]), () =>
            throw new CommandException("shell.openSettings", CommandErrorKind.Unavailable, "没有"), overlay);
        presenter.Show(LauncherSource.All);

        await presenter.OpenSettingsAsync();

        Assert.Single(overlay.HudMessages);
    }

    private static LauncherPresenter NewPresenter(ILauncherView view, Func<Task> openSettings, RecordingOverlay overlay) => new(
        new LauncherState(), () => new LauncherSettings(), view, new QuietSystem(), new RecordingLauncher(), new FakeClipboard(),
        overlay, _ => { }, NullLogger.Instance, null, openSettings);

    private sealed class LoggingView(List<string> log) : ILauncherView
    {
        public bool IsVisible { get; private set; }

        public bool IsActive => IsVisible;

        public void Render(LauncherState state)
        {
        }

        public void SetQuery(string text)
        {
        }

        public void Show(PanelPlacement placement)
        {
            IsVisible = true;
            log.Add("show");
        }

        public void Hide()
        {
            IsVisible = false;
            log.Add("hide");
        }
    }

    private sealed class QuietSystem : IPanelSystem
    {
        public nint Foreground() => 1;

        public Task RestoreAsync(nint window) => Task.CompletedTask;

        public void AllowForegroundHandoff()
        {
        }

        public PanelPlacement Locate() => new(new ScreenRect(0, 0, 1920, 1080), 1);
    }
}
