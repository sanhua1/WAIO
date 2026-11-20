using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Waio.Core.Host;
using Waio.Core.Launch;

namespace Waio.Core.Update;

public enum UpdateStatus
{
    /// <summary>已是最新。</summary>
    UpToDate,

    /// <summary>有新版本(<see cref="UpdateCheck.Release"/>)。</summary>
    Available,

    /// <summary>仓库还没有发布过版本。</summary>
    NoRelease,

    /// <summary>检查失败(网络、限流、格式不对),见 <see cref="UpdateCheck.Message"/>。</summary>
    Failed,
}

public sealed record UpdateCheck(UpdateStatus Status, ReleaseInfo? Release, string Message, DateTimeOffset CheckedAt);

/// <summary>
/// 检查更新 / 下载安装(ADR-0031):读 GitHub 的 releases/latest,有新版本时下载安装包、校验 SHA-256、静默安装。
/// 只有装在 Program Files 下、以管理员身份运行的安装版才能自动安装;开发版只能检查。
/// 检查时只访问 GitHub 的公开接口,不带任何个人信息。
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _repository;

    public UpdateService(ILogger logger, string repository = ReleaseFeed.DefaultRepository, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _repository = repository;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // 每次调用自己定超时(检查 30 秒,下载不限)
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WAIO", ReleaseFeed.Format(CurrentVersion)));
    }

    /// <summary>正在运行的版本(Directory.Build.props 的 Version)。</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version is { } version ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : new Version(0, 0, 0);

    public string ReleasesPage => $"https://github.com/{_repository}/releases";

    /// <summary>能否自动安装;不能时 <paramref name="reason"/> 说明原因。</summary>
    public static bool CanInstall(out string reason)
    {
        if (!AutostartTask.IsSafeInstallPath(Environment.ProcessPath ?? ""))
        {
            reason = "当前不是安装版(不在 Program Files 下),请到发布页下载安装包";
            return false;
        }

        if (!WindowsCapabilityProbe.IsElevated())
        {
            reason = "需要以管理员身份运行才能自动安装更新";
            return false;
        }

        reason = "";
        return true;
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellation = default) =>
        await CheckAsync(CurrentVersion, cancellation).ConfigureAwait(false);

    public async Task<UpdateCheck> CheckAsync(Version current, CancellationToken cancellation)
    {
        var now = DateTimeOffset.Now;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.LatestReleaseApi(_repository));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheck(UpdateStatus.NoRelease, null, "还没有发布过版本", now);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return new UpdateCheck(UpdateStatus.Failed, null, "GitHub 暂时限制了访问次数,稍后再试", now);
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (ReleaseFeed.Parse(json) is not { } release)
            {
                return new UpdateCheck(UpdateStatus.Failed, null, "最新的发布里没有可用的安装包", now);
            }

            return release.Version > current
                ? new UpdateCheck(UpdateStatus.Available, release, $"有新版本 {ReleaseFeed.Format(release.Version)}", now)
                : new UpdateCheck(UpdateStatus.UpToDate, release, "已是最新版本", now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            && !cancellation.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "检查更新失败");
            return new UpdateCheck(UpdateStatus.Failed, null, "检查更新失败:网络不通或 GitHub 无法访问", now);
        }
    }

    /// <summary>
    /// 下载、校验并静默运行安装包。成功时安装包会结束本进程,所以返回 null 之后不要再做别的事;失败返回原因。
    /// </summary>
    public async Task<string?> InstallAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellation)
    {
        if (!CanInstall(out var reason))
        {
            return reason;
        }

        try
        {
            using var installer = await new UpdateDownloader(_http, _logger).DownloadAsync(release, progress, cancellation).ConfigureAwait(false);
            _logger.LogInformation("更新:启动安装包 {Path}", installer.Path);
            InstallerRunner.RunSilent(installer.Path);
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); // 让安装包先打开文件,再放开句柄
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "更新失败");
            return ex is HttpRequestException ? "下载失败:网络不通或 GitHub 无法访问" : $"更新失败:{ex.Message}";
        }
    }

    public void Dispose() => _http.Dispose();
}
