using Microsoft.Extensions.Logging;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Index;

namespace Waio.Feature.Launcher;

/// <summary>启动台的文件搜索部分(P2,ADR-0024):全盘索引的启动 / 停止、命令、状态通知。</summary>
public sealed partial class LauncherFeature
{
    /// <summary>停用时等索引线程停下并存盘的最长时间(宿主给功能停用 15 秒)。</summary>
    private static readonly TimeSpan IndexStopTimeout = TimeSpan.FromSeconds(8);

    /// <summary>状态文件最多每秒写一次(扫描时进度一直在变)。</summary>
    private static readonly TimeSpan StatusWriteInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _indexGate = new();
    private FileIndex? _fileIndex;
    private IDisposable? _showFilesRegistration;
    private IDisposable? _rebuildRegistration;
    private string? _fileStatus;
    private Timer? _statusTimer;
    private int _statusPending;

    private void RegisterFileCommands()
    {
        _showFilesRegistration = context.Commands.Register(ShowFiles, (_, _) => Toggle(LauncherSource.Files));
        _rebuildRegistration = context.Commands.Register(RebuildIndex, async (_, _) =>
        {
            if (_fileIndex is not { } index)
            {
                await context.Overlay.ShowHudAsync("文件搜索没有打开");
                return;
            }

            index.Rebuild();
            context.Logger.LogInformation("文件索引:按用户要求重建");
            await context.Overlay.ShowHudAsync("正在重建文件索引…");
        });
    }

    private void StartFileIndex()
    {
        lock (_indexGate)
        {
            if (_fileIndex is not null || !_settings.FileSearchEnabled)
            {
                return;
            }

            _statusTimer ??= new Timer(_ => WriteStatus(), null, Timeout.Infinite, Timeout.Infinite);
            var index = new FileIndex(context.Storage.DataDirectory, Environment.GetEnvironmentVariable("USERPROFILE"), _settings, context.Logger);
            index.StatusChanged += OnIndexStatusChanged;
            _fileIndex = index;
            index.Start();
        }
    }

    private void StopFileIndex()
    {
        FileIndex? index;
        lock (_indexGate)
        {
            index = _fileIndex;
            _fileIndex = null;
        }

        _showFilesRegistration?.Dispose();
        _rebuildRegistration?.Dispose();
        _showFilesRegistration = _rebuildRegistration = null;
        if (index is null)
        {
            return;
        }

        index.StatusChanged -= OnIndexStatusChanged;
        index.Stop(IndexStopTimeout); // 有变化的卷在这里存盘
        index.Dispose();
        _statusTimer?.Dispose();
        _statusTimer = null;
    }

    /// <summary>设置变了:文件搜索开关在这里启停,其余(可移动卷、排除项)交给索引线程。</summary>
    private void ApplyFileSettings(LauncherSettings settings)
    {
        if (settings.FileSearchEnabled && _fileIndex is null)
        {
            if (_showFilesRegistration is null)
            {
                RegisterFileCommands();
            }

            StartFileIndex();
        }
        else if (!settings.FileSearchEnabled && _fileIndex is { } running)
        {
            lock (_indexGate)
            {
                _fileIndex = null;
            }

            running.StatusChanged -= OnIndexStatusChanged;
            _ = Task.Run(() =>
            {
                running.Stop(IndexStopTimeout);
                running.Dispose(); // 关掉文件搜索就把几百 MB 的索引放掉
                context.Logger.LogInformation("文件索引:已关闭");
            });
            SetFileStatus(null);
        }
        else
        {
            _fileIndex?.ApplySettings(settings);
        }
    }

    // 索引线程上触发:面板上的一句话立即更新,状态文件节流写
    private void OnIndexStatusChanged()
    {
        if (_fileIndex is not { } index)
        {
            return;
        }

        SetFileStatus(IndexStatusReport.Summary(index.Status()));
        if (Interlocked.Exchange(ref _statusPending, 1) == 0)
        {
            _statusTimer?.Change(StatusWriteInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void SetFileStatus(string? text)
    {
        _fileStatus = text;
        context.Ui.Post(() => _presenter?.SetFileStatus(text));
    }

    private void WriteStatus()
    {
        Interlocked.Exchange(ref _statusPending, 0);
        if (_fileIndex is not { } index)
        {
            return;
        }

        try
        {
            IndexStatusReport.Write(context.Storage.DataDirectory, index.Status());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.LogDebug(ex, "写文件索引状态失败");
        }
    }
}
