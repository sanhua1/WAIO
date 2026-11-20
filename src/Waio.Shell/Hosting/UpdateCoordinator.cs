using Microsoft.Extensions.Logging;
using Waio.Core.Update;

namespace Waio.Shell.Hosting;

/// <summary>
/// 检查更新的调度(ADR-0031):启动 2 分钟后开始,之后每小时看一眼,距上次检查满 24 小时(且开着自动检查)才真正去查。
/// 查到新版本时触发 <see cref="UpdateAvailable"/>(同一个版本只提示一次),由宿主弹托盘通知;安装要用户在设置页点。
/// 状态变化(<see cref="Changed"/>)可能在任意线程触发。
/// </summary>
internal sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly ShellContext _context;
    private readonly ILogger _logger;
    private readonly UpdateService _service;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateCoordinator(ShellContext context, ILogger logger)
    {
        _context = context;
        _logger = logger;
        _service = new UpdateService(logger);
    }

    /// <summary>状态变了(开始 / 结束检查、下载进度、结果)。</summary>
    public event Action? Changed;

    /// <summary>自动检查发现了还没提示过的新版本。</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    public string CurrentVersion => ReleaseFeed.Format(UpdateService.CurrentVersion);

    public string ReleasesPage => _service.ReleasesPage;

    /// <summary>最近一次检查的结果(本次运行内)。</summary>
    public UpdateCheck? Last { get; private set; }

    public bool IsChecking { get; private set; }

    /// <summary>正在下载时的进度(0~1),否则为 null。</summary>
    public double? DownloadProgress { get; private set; }

    public bool AutoCheck => _context.Settings.Update.AutoCheck;

    public static bool CanInstall(out string reason) => UpdateService.CanInstall(out reason);

    public void Start() => _ = RunAsync(_stop.Token);

    public async Task SetAutoCheckAsync(bool enabled)
    {
        _context.Settings.Update.AutoCheck = enabled;
        await _context.SaveAppSettingsAsync().ConfigureAwait(false);
    }

    /// <summary>立即检查(设置页按钮、托盘菜单)。正在检查时等那一次的结果。</summary>
    public async Task<UpdateCheck> CheckNowAsync() => await CheckAsync(manual: true, CancellationToken.None).ConfigureAwait(false);

    /// <summary>下载并安装 <see cref="Last"/> 里的新版本。成功时安装包会结束本进程;失败返回原因。</summary>
    public async Task<string?> InstallAsync()
    {
        if (Last is not { Status: UpdateStatus.Available, Release: { } release })
        {
            return "没有可安装的新版本";
        }

        DownloadProgress = 0;
        Changed?.Invoke();
        var progress = new Progress<double>(p =>
        {
            DownloadProgress = p;
            Changed?.Invoke();
        });
        try
        {
            return await _service.InstallAsync(release, progress, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            DownloadProgress = null;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _service.Dispose();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(FirstDelay, stop).ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                var update = _context.Settings.Update;
                if (update.AutoCheck && (update.LastCheckedAt is not { } last || DateTimeOffset.Now - last >= Interval))
                {
                    await CheckAsync(manual: false, stop).ConfigureAwait(false);
                }

                await Task.Delay(Tick, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出
        }
    }

    private async Task<UpdateCheck> CheckAsync(bool manual, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            IsChecking = true;
            Changed?.Invoke();
            var result = await _service.CheckAsync(cancellation).ConfigureAwait(false);
            Last = result;
            var settings = _context.Settings.Update;
            settings.LastCheckedAt = result.CheckedAt;
            var notify = !manual && result is { Status: UpdateStatus.Available, Release: { } release }
                && settings.NotifiedVersion != ReleaseFeed.Format(release.Version);
            if (notify)
            {
                settings.NotifiedVersion = ReleaseFeed.Format(result.Release!.Version);
            }

            await _context.SaveAppSettingsAsync().ConfigureAwait(false);
            _logger.LogInformation("检查更新:{Status} {Message}", result.Status, result.Message);
            if (notify)
            {
                UpdateAvailable?.Invoke(result.Release!);
            }

            return result;
        }
        finally
        {
            IsChecking = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }
}
