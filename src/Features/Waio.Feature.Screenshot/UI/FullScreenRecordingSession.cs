using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Recording;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Recording;
using Waio.Kit.Features;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 全屏录屏(ADR-0030):一个快捷键开始,录鼠标所在的显示器,屏幕四周一圈强调色细框(画在屏幕里面,排除在截屏之外);
/// 画面 + 系统声音 + 麦克风,每路声音单独一条音轨,另加一条混音,写成分段 MP4 临时文件。
/// 再按一次(或"结束录屏")停止 → 问保留哪些声音 → 把画面和选中的那条声音原样转封装成最终文件 → 复制到剪贴板。
/// 没有控制条、不抢键盘。除构造函数外的公开方法都只在 UI 线程上调用。
/// </summary>
internal sealed class FullScreenRecordingSession
{
    private readonly IFeatureContext _context;
    private readonly Func<ScreenshotSettings> _settings;
    private readonly Action _ended;
    private ScrollCaptureBorder? _border;
    private IDisposable? _glassHold;
    private ScreenRecorder? _recorder;
    private string? _temp;
    private string? _final;
    private bool _starting;
    private bool _stopRequested;
    private bool _done;

    public FullScreenRecordingSession(IFeatureContext context, Func<ScreenshotSettings> settings, Action ended)
    {
        _context = context;
        _settings = settings;
        _ended = ended;
    }

    public void Start()
    {
        var cursor = ScreenLocator.CursorPosition();
        var displays = ScreenCapturer.Displays();
        var display = displays.FirstOrDefault(d => d.Bounds.Contains(new PxPoint(cursor.X, cursor.Y))) ?? displays.FirstOrDefault();
        if (display is null)
        {
            Fail("录屏失败:没有找到显示器");
            return;
        }

        var settings = _settings();
        ScreenRecorder.Options options;
        try
        {
            var (cw, ch, ow, oh) = RecordingGeometry.Mp4(display.Bounds.Width, display.Bounds.Height);
            var directory = ImageExporter.RecordingDirectory(settings.RecordingDirectory);
            Directory.CreateDirectory(directory);
            _final = RecordingSession.UniquePath(directory, RecordingGeometry.FileName(DateTimeOffset.Now, RecordingFormat.Mp4));
            _temp = Path.Combine(directory, Path.GetFileNameWithoutExtension(_final) + " (录制中).mp4");
            options = new ScreenRecorder.Options(
                display.Bounds with { Width = cw, Height = ch }, (ow, oh), RecordingFormat.Mp4, Math.Clamp(settings.RecordingFps, 5, 60),
                SystemAudio: true, Microphone: true, settings.RecordCursor, _temp, SeparateTracks: true, Fragmented: true,
                Monitor: display.Monitor, MonitorBounds: display.Bounds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail($"录屏失败:{ex.Message}");
            return;
        }

        _starting = true;
        _glassHold = Waio.UI.Theming.Glass.SuspendBackgroundRefresh(); // 录制期间 WAIO 窗口不被短暂排除,录出来不闪
        var bars = ScrollCaptureLayout.InnerBorder(display.Bounds, ScrollCaptureBorder.Thickness(display.Scale));
        var color = ScrollCaptureBorder.SystemAccent();
        _ = Task.Run(() =>
        {
            try
            {
                var recorder = ScreenRecorder.Start(options, _context.Logger);
                _context.Ui.Post(() => Started(recorder, bars, color));
            }
            catch (Exception ex) when (ex is RecordingException or CaptureException or IOException or UnauthorizedAccessException)
            {
                _context.Ui.Post(() => Fail($"录屏失败:{ex.Message}"));
            }
        });
    }

    /// <summary>结束录制并询问保留哪些声音。还在启动中就等启动完再结束。</summary>
    public void Stop()
    {
        if (_starting)
        {
            _stopRequested = true;
            return;
        }

        End(keep: true);
    }

    /// <summary>功能停用:不保存、直接结束。</summary>
    public void Abort()
    {
        _stopRequested = true;
        End(keep: false);
    }

    private void Started(ScreenRecorder recorder, IReadOnlyList<PxRect> bars, uint color)
    {
        _starting = false;
        _recorder = recorder;
        if (_done || _stopRequested)
        {
            End(keep: !_done);
            return;
        }

        recorder.Failed += error => _context.Ui.Post(() => Fail($"录屏中断:{error.Message}"));
        _border = new ScrollCaptureBorder(bars, color);
        _border.Show();
        foreach (var warning in recorder.Warnings)
        {
            _ = _context.Overlay.ShowHudAsync($"{warning},这一路声音不录").AsTask();
        }
    }

    private void Fail(string message)
    {
        _starting = false;
        _ = _context.Overlay.ShowHudAsync(message).AsTask();
        End(keep: false);
    }

    private void End(bool keep)
    {
        if (_done && _recorder is null)
        {
            return;
        }

        _done = true;
        _border?.Dispose();
        _border = null;
        _glassHold?.Dispose();
        _glassHold = null;
        if (_recorder is not { } recorder)
        {
            if (!_starting)
            {
                _ended();
            }

            return; // 还在启动:Started 里会再进来
        }

        _recorder = null;
        _ended();
        var temp = _temp!;
        var final = _final!;
        _ = Task.Run(async () =>
        {
            try
            {
                var elapsed = await recorder.StopAsync(keep).ConfigureAwait(false);
                if (keep)
                {
                    var layout = recorder.AudioLayout;
                    var (systemPeak, micPeak) = recorder.Peaks;
                    _context.Ui.Post(() => Ask(elapsed, layout, systemPeak, micPeak, temp, final));
                }
            }
            catch (Exception ex) when (ex is RecordingException or IOException or UnauthorizedAccessException)
            {
                _context.Logger.LogError(ex, "全屏录屏结束失败");
                await _context.Overlay.ShowHudAsync($"录屏失败:{ex.Message}").ConfigureAwait(false);
            }
        });
    }

    private void Ask(TimeSpan elapsed, IReadOnlyList<AudioTrackKind> layout, float systemPeak, float micPeak, string temp, string final)
    {
        var system = new RecordingTracksWindow.Source(layout.Contains(AudioTrackKind.System), systemPeak < RecordingTracks.SilencePeak);
        var microphone = new RecordingTracksWindow.Source(layout.Contains(AudioTrackKind.Microphone), micPeak < RecordingTracks.SilencePeak);
        var window = new RecordingTracksWindow(
            elapsed, system, microphone,
            (keepSystem, keepMic) => _ = Task.Run(() => SaveAsync(elapsed, layout, keepSystem, keepMic, temp, final)),
            () => _ = Task.Run(() => DiscardAsync(temp, layout)));
        window.ShowAndActivate();
    }

    private async Task SaveAsync(TimeSpan elapsed, IReadOnlyList<AudioTrackKind> layout, bool system, bool microphone, string temp, string final)
    {
        try
        {
            // 画面一个文件、每路声音各一个文件:按文件挑,不会拿错轨
            var keep = RecordingTracks.Keep(layout, system, microphone);
            string[] sources = keep is { } index ? [temp, RecordingTracks.SidecarPath(temp, layout[index])] : [temp];
            Mp4Remuxer.Remux(final, sources);
            DeleteTemp(temp, layout);
            _context.Logger.LogInformation("全屏录屏已保存:{Path},声音 {Audio}", final, keep is { } i ? layout[i].ToString() : "无");

            var copied = await _context.Clipboard.CopyAsync(ClipboardContent.FromFiles([final])).ConfigureAwait(false);
            var size = new FileInfo(final).Length / 1_000_000.0;
            await _context.Overlay.ShowHudAsync(
                $"录屏已保存:{Path.GetFileName(final)}({RecordingGeometry.Elapsed(elapsed)},{size:0.#} MB){(copied ? ",已复制文件" : "")}")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RecordingException or IOException or UnauthorizedAccessException)
        {
            // 临时文件留着(分段 MP4,本身就能播放,里面有全部音轨)
            _context.Logger.LogError(ex, "保存全屏录屏失败,临时文件保留在 {Temp}", temp);
            await _context.Overlay.ShowHudAsync($"保存录屏失败:{ex.Message}。原始录像保留在 {Path.GetFileName(temp)}").ConfigureAwait(false);
        }
    }

    private async Task DiscardAsync(string temp, IReadOnlyList<AudioTrackKind> layout)
    {
        DeleteTemp(temp, layout);
        await _context.Overlay.ShowHudAsync("已放弃录屏").ConfigureAwait(false);
    }

    /// <summary>删掉画面和各路声音的临时文件;删不掉只记日志。</summary>
    private void DeleteTemp(string temp, IReadOnlyList<AudioTrackKind> layout)
    {
        foreach (var path in layout.Select(k => RecordingTracks.SidecarPath(temp, k)).Prepend(temp))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _context.Logger.LogWarning(ex, "删除录屏临时文件失败:{Temp}", path);
            }
        }
    }
}
