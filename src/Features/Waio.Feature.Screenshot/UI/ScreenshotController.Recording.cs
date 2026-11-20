using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 录屏(MAIO ScreenshotController+Recording,ADR-0021):框选后在选区上开始录屏;录屏命令再触发一次就完成。
/// 全屏录屏(ADR-0030):不框选,直接录鼠标所在的显示器;同一个命令再触发一次就结束。同一时刻只有一个录屏。
/// </summary>
internal sealed partial class ScreenshotController
{
    private RecordingSession? _recording; // 只在 UI 线程访问
    private FullScreenRecordingSession? _fullScreenRecording; // 只在 UI 线程访问

    /// <summary>录屏命令(默认 Alt+F3):录制中(或正在选格式)就完成 / 开始,否则先框选。正在全屏录屏时 = 结束全屏录屏。</summary>
    public async Task RecordAsync()
    {
        if (await OnUiAsync(() =>
            {
                if (_fullScreenRecording is { } fullScreen)
                {
                    fullScreen.Stop();
                    return true;
                }

                if (_recording is null)
                {
                    return false;
                }

                _recording.Toggle();
                return true;
            }).ConfigureAwait(false))
        {
            return;
        }

        await CaptureAsync(CaptureAction.Record).ConfigureAwait(false);
    }

    /// <summary>全屏录屏命令(默认 Ctrl+Alt+F2):没在录就开始,正在全屏录就结束。正在区域录屏时提示一下,不另开。</summary>
    public Task RecordFullScreenAsync() => OnUiAsync(() =>
    {
        if (_fullScreenRecording is { } active)
        {
            active.Stop();
            return;
        }

        if (_recording is not null)
        {
            _ = context.Overlay.ShowHudAsync("正在录屏,先结束当前录屏").AsTask();
            return;
        }

        FullScreenRecordingSession? session = null;
        session = new FullScreenRecordingSession(context, () => Settings, () =>
        {
            if (ReferenceEquals(_fullScreenRecording, session))
            {
                _fullScreenRecording = null;
            }
        });
        _fullScreenRecording = session;
        session.Start();
    });

    /// <summary>"结束录屏"命令:结束正在进行的录屏(全屏或区域);区域录屏还在选格式时不动。</summary>
    public Task StopRecordingAsync() => OnUiAsync(() =>
    {
        if (_fullScreenRecording is { } fullScreen)
        {
            fullScreen.Stop();
        }
        else if (_recording is { IsSettingUp: false } region)
        {
            region.Toggle();
        }
    });

    /// <summary>UI 线程。</summary>
    private void BeginRecording(CaptureResult result)
    {
        if (_recording is not null || _fullScreenRecording is not null)
        {
            return;
        }

        var frame = result.Frame;
        var center = new PxPoint(frame.CenterX, frame.CenterY);
        var display = ScreenCapturer.Displays().FirstOrDefault(d => d.Bounds.Contains(center))?.Bounds ?? frame;
        RecordingSession? session = null;
        session = new RecordingSession(frame, display, result.Scale, context, () => Settings, s => _ = context.Settings.SaveAsync(s).AsTask(), () =>
        {
            if (ReferenceEquals(_recording, session))
            {
                _recording = null;
            }
        });
        _recording = session;
        session.Show();
    }
}
