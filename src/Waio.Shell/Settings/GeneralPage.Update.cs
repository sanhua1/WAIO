using Microsoft.UI.Xaml;
using Waio.Core.Update;
using Waio.Shell.Hosting;

namespace Waio.Shell.Settings;

/// <summary>通用页的"更新"卡片(ADR-0031):当前版本、自动检查开关、立即检查、下载并安装、打开发布页面。</summary>
internal sealed partial class GeneralPage
{
    private bool _refreshingUpdateSwitch;

    private UpdateCoordinator Updates => _context.Updates;

    private void InitUpdateCard()
    {
        VersionText.Text = $"当前版本 {Updates.CurrentVersion}";
        _refreshingUpdateSwitch = true;
        AutoUpdateSwitch.IsOn = Updates.AutoCheck;
        _refreshingUpdateSwitch = false;
        Updates.Changed -= OnUpdateChanged;
        Updates.Changed += OnUpdateChanged;
        RefreshUpdateCard();
    }

    /// <summary>可能在任意线程触发。</summary>
    private void OnUpdateChanged() => DispatcherQueue.TryEnqueue(RefreshUpdateCard);

    private void RefreshUpdateCard()
    {
        var last = Updates.Last;
        var busy = Updates.IsChecking || Updates.DownloadProgress is not null;
        CheckUpdateButton.IsEnabled = !busy;
        UpdateProgress.Opacity = Updates.DownloadProgress is null ? 0 : 1;
        UpdateProgress.Value = Updates.DownloadProgress ?? 0;

        var available = last is { Status: UpdateStatus.Available, Release: not null };
        var canInstall = UpdateCoordinator.CanInstall(out var reason);
        InstallUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = available && canInstall && !busy;
        ReleaseNotesButton.Content = available ? "查看更新说明" : "查看发布页面";

        UpdateMessage.Text = Updates.DownloadProgress is { } p ? $"正在下载 {p:P0}……"
            : Updates.IsChecking ? "正在检查……"
            : last is null ? LastCheckedText()
            : available && !canInstall ? $"{last.Message}。{reason}"
            : available ? $"{last.Message}(发布于 {last.Release!.PublishedAt:yyyy-MM-dd}),点\"下载并安装\",安装完会自动重新启动"
            : last.Message;
    }

    private string LastCheckedText() => _context.Settings.Update.LastCheckedAt is { } at
        ? $"上次检查:{at.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "";

    private async void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (!_refreshingUpdateSwitch)
        {
            await Updates.SetAutoCheckAsync(AutoUpdateSwitch.IsOn);
        }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e) => await Updates.CheckNowAsync();

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        var error = await Updates.InstallAsync();
        UpdateMessage.Text = error ?? "正在安装,WAIO 马上会关闭并重新启动……";
    }

    private async void OnOpenReleaseNotes(object sender, RoutedEventArgs e)
    {
        var page = Updates.Last?.Release?.PageUrl;
        await _context.OpenAsync(string.IsNullOrEmpty(page) ? Updates.ReleasesPage : page);
    }
}
