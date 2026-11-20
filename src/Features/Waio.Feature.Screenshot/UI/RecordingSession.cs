using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Domain.Recording;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Feature.Screenshot.Infrastructure.Recording;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 一次录屏的交互(MAIO RecordingSession,ADR-0021):选区外的边框 + 控制条。
/// 开始前用键盘会话接管 ⏎ / Esc(按键不会打到下面的程序);开始后立即交还键盘,录制中的按键照常打到其他程序。
/// 结束:控制条上的"完成",或再触发一次 <c>screenshot.record</c>;"放弃"删除文件。
/// 结果存到录屏文件夹,文件复制到剪贴板;不写入截图历史(MAIO 同)。
/// 除构造函数外的公开方法都只在 UI 线程上调用。
/// </summary>
internal sealed class RecordingSession
{
    private readonly PxRect _selection;
    private readonly double _scale;
    private readonly IFeatureContext _context;
    private readonly Func<ScreenshotSettings> _settings;
    private readonly Action<ScreenshotSettings> _save;
    private readonly Action _ended;
    private readonly RecordingBar _bar;
    private ScrollCaptureBorder _border;
    private IKeyboardSession? _keyboard;
    private ScreenRecorder? _recorder;

    /// <summary>录制期间暂停自绘玻璃取背景(ADR-0027)。</summary>
    private IDisposable? _glassHold;
    private DispatcherTimer? _timer;
    private string? _path;
    private bool _starting;
    private bool _done;

    public RecordingSession(
        PxRect selection, PxRect displayBounds, double scale, IFeatureContext context,
        Func<ScreenshotSettings> settings, Action<ScreenshotSettings> save, Action ended)
    {
        _selection = selection;
        _scale = scale;
        _context = context;
        _settings = settings;
        _save = save;
        _ended = ended;
        var s = settings();
        _border = new ScrollCaptureBorder(selection, scale);
        _bar = new RecordingBar(selection, displayBounds, scale, new RecordingBar.Choices(s.RecordingFormat, s.RecordSystemAudio, s.RecordMicrophone),
            new RecordingBar.Actions(Begin, Cancel, Finish, Discard, Remember));
    }

    /// <summary>还在选格式、没开始录。</summary>
    public bool IsSettingUp => _recorder is null && !_done;

    public void Show()
    {
        _border.Show();
        _bar.ShowBar();
        _keyboard = _context.Input.BeginKeyboardSession(new KeyboardSessionOptions(ConsumeEvents: true));
        _ = ListenAsync(_keyboard);
    }

    /// <summary>录屏命令再次触发:没开始就开始,录制中就完成。</summary>
    public void Toggle()
    {
        if (_recorder is null)
        {
            Begin();
        }
        else
        {
            Finish();
        }
    }

    /// <summary>功能停用:不保存、直接结束。</summary>
    public void Abort() => End(keep: false, announce: false);

    private void Remember(RecordingBar.Choices choices)
    {
        var settings = _settings();
        settings.RecordingFormat = choices.Format;
        settings.RecordSystemAudio = choices.SystemAudio;
        settings.RecordMicrophone = choices.Microphone;
        _save(settings);
    }

    private void Begin()
    {
        if (_starting || _recorder is not null || _done)
        {
            return;
        }

        _starting = true;
        ReleaseKeyboard();
        var choices = _bar.Selection;
        var settings = _settings();
        ScreenRecorder.Options options;
        try
        {
            options = Options(choices, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail($"录屏失败:{ex.Message}");
            return;
        }

        _path = options.Path;
        _glassHold ??= Waio.UI.Theming.Glass.SuspendBackgroundRefresh(); // 录制期间 WAIO 窗口不被短暂排除,录出来不闪
        _ = Task.Run(() =>
        {
            try
            {
                var recorder = ScreenRecorder.Start(options, _context.Logger);
                _context.Ui.Post(() => Started(recorder));
            }
            catch (Exception ex) when (ex is RecordingException or CaptureException or IOException or UnauthorizedAccessException)
            {
                _context.Ui.Post(() => Fail($"录屏失败:{ex.Message}"));
            }
        });
    }

    private ScreenRecorder.Options Options(RecordingBar.Choices choices, ScreenshotSettings settings)
    {
        var mp4 = choices.Format == RecordingFormat.Mp4;
        PxRect rect;
        (int, int) output;
        if (mp4)
        {
            var (cw, ch, ow, oh) = RecordingGeometry.Mp4(_selection.Width, _selection.Height);
            rect = _selection with { Width = cw, Height = ch };
            output = (ow, oh);
        }
        else
        {
            rect = _selection;
            output = RecordingGeometry.Gif(_selection.Width, _selection.Height, _scale, settings.GifHighQuality);
        }

        var directory = ImageExporter.RecordingDirectory(settings.RecordingDirectory);
        Directory.CreateDirectory(directory);
        var path = UniquePath(directory, RecordingGeometry.FileName(DateTimeOffset.Now, choices.Format));
        var fps = mp4 ? Math.Clamp(settings.RecordingFps, 5, 60) : Math.Clamp(settings.GifFps, 5, 30);
        return new ScreenRecorder.Options(rect, output, choices.Format, fps, mp4 && choices.SystemAudio, mp4 && choices.Microphone, settings.RecordCursor, path);
    }

    /// <summary>文件夹里不重名的路径(重名时加" (2)"…)。</summary>
    internal static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        }

        return path;
    }

    private void Started(ScreenRecorder recorder)
    {
        _starting = false;
        if (_done)
        {
            _ = recorder.StopAsync(keep: false); // 开始的过程中被取消了
            return;
        }

        _recorder = recorder;
        recorder.Failed += error => _context.Ui.Post(() => Fail($"录屏中断:{error.Message}"));
        _border.Dispose();
        _border = new ScrollCaptureBorder(_selection, _scale, ScrollCaptureBorder.RecordingColor);
        _border.Show();
        _bar.ShowRecording();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _bar.SetElapsed(recorder.Elapsed);
        _timer.Start();
        foreach (var warning in recorder.Warnings)
        {
            _ = _context.Overlay.ShowHudAsync($"{warning},这一路声音不录").AsTask();
        }
    }

    private void Cancel() => End(keep: false, announce: false);

    private void Finish() => End(keep: true, announce: true);

    private void Discard() => End(keep: false, announce: true);

    private void Fail(string message)
    {
        _starting = false;
        _ = _context.Overlay.ShowHudAsync(message).AsTask();
        End(keep: false, announce: false);
    }

    private void End(bool keep, bool announce)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _glassHold?.Dispose();
        _glassHold = null;
        ReleaseKeyboard();
        _timer?.Stop();
        _border.Dispose();
        _bar.Close();
        _ended();
        if (_recorder is not { } recorder)
        {
            return;
        }

        var path = _path!;
        _ = Task.Run(async () =>
        {
            try
            {
                var elapsed = await recorder.StopAsync(keep).ConfigureAwait(false);
                if (!keep)
                {
                    if (announce)
                    {
                        await _context.Overlay.ShowHudAsync("已放弃录屏").ConfigureAwait(false);
                    }

                    return;
                }

                // 文件复制到剪贴板(MAIO 同),可以直接粘贴到聊天窗口、资源管理器
                var copied = await _context.Clipboard.CopyAsync(ClipboardContent.FromFiles([path])).ConfigureAwait(false);
                var size = new FileInfo(path).Length / 1_000_000.0;
                await _context.Overlay.ShowHudAsync(
                    $"录屏已保存:{Path.GetFileName(path)}({RecordingGeometry.Elapsed(elapsed)},{size:0.#} MB){(copied ? ",已复制文件" : "")}")
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RecordingException or IOException or UnauthorizedAccessException)
            {
                _context.Logger.LogError(ex, "保存录屏失败");
                await _context.Overlay.ShowHudAsync($"保存录屏失败:{ex.Message}").ConfigureAwait(false);
            }
        });
    }

    private void ReleaseKeyboard()
    {
        _ = _keyboard?.DisposeAsync();
        _keyboard = null;
    }

    private async Task ListenAsync(IKeyboardSession keyboard)
    {
        try
        {
            await foreach (var e in keyboard.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (e is { Kind: KeyEventKind.Down, IsRepeat: false, Key: "enter" })
                {
                    _context.Ui.Post(Begin);
                }
                else if (e is { Kind: KeyEventKind.Down, IsRepeat: false, Key: "esc" })
                {
                    _context.Ui.Post(Cancel);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // 会话结束
        }
    }
}
