using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Waio.Feature.Clipboard.Infrastructure;
using Waio.Feature.Clipboard.Panel;
using Waio.Feature.Clipboard.Views;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Clipboard;

/// <summary>
/// 剪贴板历史(参考 MAIO,ADR-0009)。本类只负责接线:
/// 剪贴板变化 → 后台队列 → <see cref="ClipHistory"/> 落盘;快捷键 → UI 线程上的 <see cref="PanelPresenter"/>。
/// 去重/保留/搜索规则在 <see cref="ClipHistory"/>,交互流程在 <see cref="PanelPresenter"/>,键位在 PanelKeys。
/// </summary>
public sealed class ClipboardFeature(IFeatureContext context) : IFeature
{
    public static readonly CommandId Show = "clipboard.show";

    /// <summary>清空历史(固定的保留)。设置页的"清空历史"按钮走这个命令,也可以绑定快捷键。</summary>
    public static readonly CommandId ClearHistory = "clipboard.clearHistory";

    public static FeatureManifest Manifest { get; } = new(
        "clipboard",
        "剪贴板",
        """
        复制过的东西都记下来,随时找回再粘贴:
        按 Win+V 弹出历史,打字或 Ctrl+F 搜索,⏎ 粘贴到正在输入的位置
        记录文字、富文本、图片和文件路径;常用的内容可以固定,不会被清理
        空格预览,Ctrl+数字键 快速粘贴前几条
        """,
        "", // Segoe Fluent Icons: Copy
        Capability.None,
        [new CommandDescriptor(Show, "显示剪贴板历史"), new CommandDescriptor(ClearHistory, "清空剪贴板历史(保留固定的)")],
        [new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("win+v")), Show)]);

    public static FeatureDescriptor Descriptor { get; } = new(Manifest, ctx => new ClipboardFeature(ctx));

    /// <summary>设置窗口里"剪贴板"页的设置部分(ADR-0010)。</summary>
    public static FeatureSettingsPage SettingsPage { get; } = new(Manifest.Id, ClipboardSettingsView.Create);

    private ClipboardSettings _settings = new();
    private SqliteClipStore? _store;
    private ClipHistory? _history;
    private Channel<ClipboardChange>? _queue;
    private Task? _consumer;
    private IDisposable? _registration;
    private IDisposable? _clearRegistration;

    // 以下两个只在 UI 线程访问
    private ClipboardPanelWindow? _window;
    private PanelPresenter? _presenter;

    public ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        _settings = context.Settings.Get<ClipboardSettings>();
        _store = SqliteClipStore.Open(context.Storage.DataDirectory, context.Logger);
        _history = new ClipHistory(_store, () => _settings, TimeProvider.System);
        context.Clipboard.RespectExclusionFormats = _settings.RespectExclusionFormats;

        _queue = Channel.CreateUnbounded<ClipboardChange>(new UnboundedChannelOptions { SingleReader = true });
        _consumer = Task.Run(ConsumeAsync, CancellationToken.None);

        context.Settings.Changed += OnSettingsChanged;
        context.Clipboard.Changed += OnClipboardChanged;
        _registration = context.Commands.Register(Show, (_, _) => ShowAsync());
        _clearRegistration = context.Commands.Register(ClearHistory, (_, _) => ClearHistoryAsync());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeactivateAsync()
    {
        context.Clipboard.Changed -= OnClipboardChanged;
        context.Settings.Changed -= OnSettingsChanged;
        _registration?.Dispose();
        _clearRegistration?.Dispose();

        _queue?.Writer.TryComplete();
        if (_consumer is not null)
        {
            await _consumer.ConfigureAwait(false); // 队列里已有的内容处理完再关库
        }

        context.Ui.Post(() =>
        {
            _window?.Close();
            _window = null;
            _presenter = null;
        });

        _store?.Dispose();
        _store = null;
    }

    // ---- 记录 ----

    // 在剪贴板线程上同步调用:只入队,绝不做 IO
    private void OnClipboardChanged(ClipboardChange change) => _queue?.Writer.TryWrite(change);

    private async Task ConsumeAsync()
    {
        var history = _history!;
        await foreach (var change in _queue!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var result = history.Record(change);
                if (result.Outcome != RecordOutcome.Ignored)
                {
                    context.Ui.Post(() => _ = _presenter?.NotifyHistoryChangedAsync());
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "记录剪贴板内容失败(已忽略,继续记录后面的)");
            }
        }
    }

    private void OnSettingsChanged()
    {
        _settings = context.Settings.Get<ClipboardSettings>();
        context.Clipboard.RespectExclusionFormats = _settings.RespectExclusionFormats;
        var history = _history;
        if (history is not null)
        {
            // 上限调小后立即清理一次,不等下一次复制
            _ = Task.Run(() => history.Enforce(_settings.StorageLimit));
        }
    }

    private async ValueTask ClearHistoryAsync()
    {
        var history = _history;
        if (history is null)
        {
            return;
        }

        await Task.Run(history.ClearUnpinned).ConfigureAwait(false);
        context.Logger.LogInformation("剪贴板历史已清空(固定的保留)");
        context.Ui.Post(() => _ = _presenter?.NotifyHistoryChangedAsync());
        await context.Overlay.ShowHudAsync("剪贴板历史已清空").ConfigureAwait(false);
    }

    // ---- 面板 ----

    private ValueTask ShowAsync()
    {
        // 窗口只能在 UI 线程创建和操作;快捷键命令来自后台分发线程
        context.Ui.Post(() => _ = TogglePanelAsync());
        return ValueTask.CompletedTask;
    }

    private async Task TogglePanelAsync()
    {
        try
        {
            if (_presenter is null)
            {
                _window = new ClipboardPanelWindow(context.Logger);
                _presenter = new PanelPresenter(
                    _history!, () => _settings, context.Clipboard, context.Input, context.Overlay,
                    _window, new WindowsWindowFocus(), new ScreenPlacement(), context.Logger);
                _window.Attach(_presenter);
            }

            await _presenter.ToggleAsync();
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "显示剪贴板面板失败");
            await context.Overlay.ShowHudAsync("剪贴板面板打开失败,详见日志");
        }
    }
}
