using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Waio.Feature.Screenshot.Domain;
using Waio.Feature.Screenshot.Infrastructure;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Feature.Screenshot.UI;

/// <summary>
/// 一次长截图(MAIO ScrollCaptureSession,ADR-0020):用户自己用滚轮往下滚,WAIO 不发任何滚动事件。
/// 后台线程不停地截选区,和参照帧(最后一次接上的帧)比较,接上新露出的部分;右侧面板显示长条预览。
/// 对不上时参照帧不变:滚回去重新有重叠就能继续,不会拼错。往回滚不算错误:落在已拼范围里只更新位置,超过起点向画布头部扩展。
/// 结束:点完成、Esc / ⏎、高度达到 <see cref="MaximumHeight"/>。
/// <see cref="Start"/>、<see cref="Finish"/>、<see cref="Cancel"/> 只在 UI 线程上调用;拼接器只在取帧线程上碰。
/// </summary>
internal sealed class ScrollCaptureSession
{
    /// <summary>最长 30000 像素(同 MAIO)。</summary>
    public const int MaximumHeight = 30000;

    /// <summary>处理完一帧后至少等这么久再截下一帧(MAIO 30 ms;BitBlt 截一块区域只要几毫秒)。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(30);

    /// <summary>开始后等这么久再截第一帧。</summary>
    private static readonly TimeSpan FirstFrameDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>预览最多这么久刷新一次(合成预览、写进位图都要时间,没必要每帧都做)。</summary>
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(150);

    private readonly PxRect _rect;
    private readonly IInputService _input;
    private readonly IUiThread _ui;
    private readonly ILogger _logger;
    private readonly Action<PixelBuffer?> _completion;
    private readonly ScrollCaptureBorder _border;
    private readonly ScrollCaptureSidebar _sidebar;
    private readonly CancellationTokenSource _stop = new();
    private readonly int _previewWidth;
    private IKeyboardSession? _keyboard;
    private Task<LongImageBuilder?>? _loop;
    private bool _finished;

    /// <param name="rect">选区(全局物理像素)。</param>
    /// <param name="work">选区所在显示器的工作区(放右侧面板)。</param>
    /// <param name="completion">在 UI 线程上调用;没截到内容时参数为 null。</param>
    public ScrollCaptureSession(
        PxRect rect, double scale, PxRect work, IInputService input, IUiThread ui, ILogger logger, Action<PixelBuffer?> completion)
    {
        _rect = rect;
        _input = input;
        _ui = ui;
        _logger = logger;
        _completion = completion;
        _border = new ScrollCaptureBorder(rect, scale);
        var width = (int)Math.Round(ScrollCaptureSidebar.WidthDip * scale);
        var area = ScrollCaptureLayout.Sidebar(rect, work, width, (int)Math.Round(10 * scale), (int)Math.Round(160 * scale));
        _sidebar = new ScrollCaptureSidebar(area, scale, Finish);
        _previewWidth = (int)Math.Round((ScrollCaptureSidebar.WidthDip - 22) * scale * 2); // 预览按两倍存,缩小显示更清楚
    }

    public void Start()
    {
        _glassHold = Waio.UI.Theming.Glass.SuspendBackgroundRefresh(); // 长截图期间 WAIO 窗口不被短暂排除
        _border.Show();
        _sidebar.ShowPanel();

        // 键盘会话期间的按键都被吞掉,不会打到下面的程序(MAIO 同);只认 Esc、⏎
        _keyboard = _input.BeginKeyboardSession(new KeyboardSessionOptions(ConsumeEvents: true));
        _ = ListenAsync(_keyboard);
        _loop = Task.Run(() => Run(_stop.Token));
    }

    /// <summary>不出结果地结束(功能停用时)。</summary>
    public void Cancel()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _stop.Cancel();
        CleanUp();
    }

    /// <summary>结束并合成长图,交给 completion。</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _stop.Cancel();
        CleanUp();
        var loop = _loop;
        _ = Task.Run(async () =>
        {
            PixelBuffer? image = null;
            try
            {
                var builder = loop is null ? null : await loop.ConfigureAwait(false);
                image = builder?.Compose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "长截图合成失败");
            }

            if (!_ui.Post(() => _completion(image)))
            {
                _logger.LogWarning("长截图结束时 UI 线程不可用,结果丢弃");
            }
        });
    }

    /// <summary>长截图期间暂停自绘玻璃取背景(ADR-0027)。</summary>
    private IDisposable? _glassHold;

    private void CleanUp()
    {
        _glassHold?.Dispose();
        _glassHold = null;
        _ = _keyboard?.DisposeAsync();
        _keyboard = null;
        _border.Dispose();
        _sidebar.Close();
    }

    private async Task ListenAsync(IKeyboardSession keyboard)
    {
        try
        {
            await foreach (var e in keyboard.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (e is { Kind: KeyEventKind.Down, IsRepeat: false, Key: "esc" or "enter" })
                {
                    _ui.Post(Finish);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // 会话结束
        }
    }

    // ---- 取帧线程 ----

    private LongImageBuilder? Run(CancellationToken stop)
    {
        LongImageBuilder? builder = null;
        GrayFrame? reference = null;
        string? warning = null;
        var lastPreview = Stopwatch.StartNew();
        var stalePreview = false;
        var anyMoved = false;

        // 截图遮罩刚刚挪走、原来的程序刚切回前台,等画面稳定再截第一帧
        if (stop.WaitHandle.WaitOne(FirstFrameDelay))
        {
            return null;
        }

        var stats = new Stats();
        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                // 第一帧也用同一种方式截,不用冻结画面(MAIO:两种截图方式的颜色处理可能有细微差别,会让逐像素比较失败)
                var frame = ScreenCapturer.Capture(_rect, layered: false);
                var gray = GrayFrame.FromBgra(frame.Pixels, frame.Width, frame.Height, ScrollMatcher.FineWidth);
                ScrollOutcome? outcome = null;
                if (builder is not null && reference is not null)
                {
                    var matchStarted = Stopwatch.GetTimestamp();
                    outcome = ScrollMatcher.Match(reference, gray);
                    stats.Add(outcome, Stopwatch.GetElapsedTime(matchStarted));
                }

                // 还没接上过任何一帧时对不上:多半是第一帧截到了正在消失的遮罩,或者页面在开始时自己变了。
                // 这时什么都还没拼,直接换成新的第一帧,否则参照帧永远对不上
                if (builder is null || reference is null || outcome is null || (!anyMoved && outcome is ScrollOutcome.Unknown))
                {
                    builder = new LongImageBuilder(frame, _previewWidth);
                    reference = gray;
                    Report(builder, builder.Preview(), null);
                    lastPreview.Restart();
                }
                else
                {
                    var newWarning = warning;
                    var moved = false; // 往回滚(画布不变)也要刷新绿框
                    switch (outcome)
                    {
                        case ScrollOutcome.Moved match:
                            newWarning = null;
                            stalePreview |= builder.Append(frame, match.Match.Offset);
                            reference = gray;
                            moved = true;
                            anyMoved = true;
                            break;
                        case ScrollOutcome.Unchanged:
                            newWarning = null;
                            break;
                        default:
                            newWarning = "没对上:滚得太快了,或者页面自己变了。\n滚回上次停下的位置,再慢一点往下滚";
                            break;
                    }

                    // 预览节流:画布变了但刚刷新过时先记着,下一次(哪怕这一帧没动)再补上
                    var preview = stalePreview && lastPreview.Elapsed >= PreviewInterval ? builder.Preview() : null;
                    if (preview is not null)
                    {
                        stalePreview = false;
                        lastPreview.Restart();
                    }

                    if (moved || preview is not null || newWarning != warning)
                    {
                        warning = newWarning;
                        Report(builder, preview, warning);
                    }

                    if (builder.Height >= MaximumHeight)
                    {
                        _ui.Post(Finish);
                        break;
                    }
                }
            }
            catch (CaptureException ex)
            {
                // 锁屏、切换桌面时拿不到屏幕:等一下再试,已经拼好的不丢
                if (warning is null && builder is not null)
                {
                    warning = $"截图失败:{ex.Message}";
                    Report(builder, null, warning);
                }
            }

            var rest = Interval - Stopwatch.GetElapsedTime(started);
            if (stop.WaitHandle.WaitOne(rest > TimeSpan.Zero ? rest : TimeSpan.FromMilliseconds(5)))
            {
                break;
            }
        }

        if (builder is not null)
        {
            _logger.LogInformation("长截图结束:{Width}×{Height},{Stats}", builder.Width, builder.Height, stats);
        }

        return builder;
    }

    private void Report(LongImageBuilder builder, PixelBuffer? preview, string? warning)
    {
        var height = builder.Height;
        var frameHeight = builder.Plan.FrameHeight;
        var position = builder.Plan.Position;
        _ui.Post(() =>
        {
            if (!_finished)
            {
                _sidebar.Update(height, frameHeight, position, preview, warning);
            }
        });
    }

    /// <summary>每帧匹配的统计(写日志,方便用户反馈"对不上"时排查)。</summary>
    private sealed class Stats
    {
        private int _moved;
        private int _unchanged;
        private int _unknown;
        private TimeSpan _total;
        private TimeSpan _max;

        public void Add(ScrollOutcome outcome, TimeSpan elapsed)
        {
            switch (outcome)
            {
                case ScrollOutcome.Moved:
                    _moved++;
                    break;
                case ScrollOutcome.Unchanged:
                    _unchanged++;
                    break;
                default:
                    _unknown++;
                    break;
            }

            _total += elapsed;
            _max = elapsed > _max ? elapsed : _max;
        }

        public override string ToString()
        {
            var count = _moved + _unchanged + _unknown;
            var average = count == 0 ? 0 : _total.TotalMilliseconds / count;
            return $"比较 {count} 帧(接上 {_moved}、没动 {_unchanged}、对不上 {_unknown}),匹配平均 {average:F1} ms、最长 {_max.TotalMilliseconds:F0} ms";
        }
    }
}
