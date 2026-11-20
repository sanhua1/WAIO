using Microsoft.Extensions.Logging;
using Waio.Feature.Translate.Domain;
using Waio.Feature.Translate.Infrastructure;
using Waio.Feature.Translate.Views;
using Waio.Kit.Commands;
using Waio.Kit.Contracts;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Feature.Translate;

/// <summary>
/// 翻译(严格参考 MAIO FeatureTranslate,ADR-0014):选中文字按 Win+T 翻译,没有选中时打开输入框。
/// 多个翻译源并行,大模型逐字显示。本类只做接线与命令调度(MAIO <c>TranslateFeature</c> + <c>TranslateCommands</c>);
/// 状态与规则在 <see cref="TranslateSession"/>,窗口在 <see cref="TranslateWindow"/>。
/// </summary>
public sealed class TranslateFeature(IFeatureContext context) : IFeature
{
    public static readonly CommandId Show = "translate.show";
    public static readonly CommandId Selection = "translate.selection";
    public static readonly CommandId Input = "translate.input";

    public static FeatureManifest Manifest { get; } = new(
        "translate",
        "翻译",
        """
        选中文字就能翻译,多个翻译源同时给出结果:
        选中文字按 Win+T 翻译;没有选中时打开输入框
        支持 Google、必应、DeepSeek 等翻译源,结果并排对比
        截图时按 Ctrl+T 可以直接翻译图片里的文字
        """,
        "", // Segoe Fluent Icons: Characters
        Capability.Network,
        [
            new CommandDescriptor(Show, "翻译"),
            new CommandDescriptor(Selection, "翻译选中的文字"),
            new CommandDescriptor(Input, "打开翻译输入框"),
            new CommandDescriptor(TranslateContracts.TranslateText.Id, "翻译指定的文字",
                [new ParameterSpec("text", "文字", ParameterKind.String, Required: true)]),
        ],
        [new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("win+t")), Show)]);

    public static FeatureDescriptor Descriptor { get; } = new(Manifest, ctx => new TranslateFeature(ctx));

    /// <summary>设置窗口里"翻译"页的设置部分(ADR-0010)。</summary>
    public static FeatureSettingsPage SettingsPage { get; } = new(Manifest.Id, TranslateSettingsView.Create);

    private static readonly TimeSpan LocateTimeout = TimeSpan.FromMilliseconds(250);

    private readonly List<IDisposable> _registrations = [];
    private ProviderFactory? _factory;
    private HistoryStore? _history;
    private SelectionReader? _reader;

    // 以下只在 UI 线程访问
    private TranslateSession? _session;
    private TranslateWindow? _window;

    internal TranslateSettings LoadSettings() => context.Settings.Get<TranslateSettings>().Normalized();

    public ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        _factory = new ProviderFactory(context.Secrets);
        _history = HistoryStore.For(context.Storage.DataDirectory, context.Logger);
        _reader = new SelectionReader(
            ct => ScreenLocator.SelectedTextAsync(LocateTimeout), context.Clipboard, context.Input);
        _session = new TranslateSession(
            LoadSettings, _factory.Make, (entry, limit) => _history.Add(entry, limit), action => context.Ui.Post(action));

        _registrations.Add(context.Commands.Register(Show, (_, ct) => ShowByBehaviorAsync(ct)));
        _registrations.Add(context.Commands.Register(Selection, (_, ct) => TranslateSelectionAsync(fallbackToInput: false, ct)));
        _registrations.Add(context.Commands.Register(Input, async (_, _) => await OnUiAsync(() => ShowWindow("", ShowTrigger.Input)).ConfigureAwait(false)));
        _registrations.Add(context.Commands.Register(TranslateContracts.TranslateText, async (input, _) =>
        {
            await OnUiAsync(() => ShowWindow(input.Text, ShowTrigger.Input)).ConfigureAwait(false);
            return default(Unit);
        }));
        _factory.Prewarm(LoadSettings().Services);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeactivateAsync()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        await OnUiAsync(() =>
        {
            _window?.TearDown();
            _window = null;
            _session?.Cancel();
        }).ConfigureAwait(false);
        _factory?.Dispose();
        _factory = null;
    }

    private async ValueTask ShowByBehaviorAsync(CancellationToken cancellationToken)
    {
        switch (LoadSettings().HotkeyBehavior)
        {
            case HotkeyBehavior.Smart:
                await TranslateSelectionAsync(fallbackToInput: true, cancellationToken).ConfigureAwait(false);
                break;
            case HotkeyBehavior.SelectionOnly:
                await TranslateSelectionAsync(fallbackToInput: false, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await OnUiAsync(() => ShowWindow("", ShowTrigger.Input)).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask TranslateSelectionAsync(bool fallbackToInput, CancellationToken cancellationToken)
    {
        // 焦点在翻译窗口里时,不去读其他程序的选中文字,直接重新翻译输入框内容(MAIO)
        if (await OnUiAsync(() => _window?.IsFocused == true).ConfigureAwait(false))
        {
            await OnUiAsync(() => _session?.Translate()).ConfigureAwait(false);
            return;
        }

        // 先记下选中文字的位置("所选文字旁边"用),再读文字。Windows 上拿的是文字光标的位置(选中文字的末端)
        Box? rect = null;
        if (LoadSettings().SelectionWindowPosition == SelectionPosition.NearSelection
            && await ScreenLocator.CaretRectAsync(ScreenLocator.ForegroundWindow(), LocateTimeout).ConfigureAwait(false) is { } caret)
        {
            rect = new Box(caret.Left, caret.Top, caret.Right, caret.Bottom);
        }

        var text = _reader is null ? null : await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (text is not null)
        {
            await OnUiAsync(() => ShowWindow(text, new ShowTrigger(true, rect))).ConfigureAwait(false);
        }
        else if (fallbackToInput)
        {
            await OnUiAsync(() => ShowWindow("", ShowTrigger.Input)).ConfigureAwait(false);
        }
        else
        {
            await context.Overlay.ShowHudAsync("没有选中的文字").ConfigureAwait(false);
        }
    }

    /// <summary>在 UI 线程上显示窗口(第一次用时创建)。</summary>
    private void ShowWindow(string? text, ShowTrigger trigger)
    {
        if (_session is null)
        {
            return; // 已停用
        }

        if (_window is null)
        {
            _window = new TranslateWindow(_session, LoadSettings, settings => _ = SaveAsync(settings))
            {
                CopyText = text => context.Clipboard.SetAsync(ClipboardContent.FromText(text)).AsTask(),
            };
        }

        _window.Show(text, trigger);
    }

    private async Task SaveAsync(TranslateSettings settings)
    {
        try
        {
            await context.Settings.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.LogError(ex, "保存翻译设置失败");
        }
    }

    private Task OnUiAsync(Action action) => OnUiAsync(() =>
    {
        action();
        return true;
    });

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        if (context.Ui.IsCurrent)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = context.Ui.Post(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        if (!posted)
        {
            completion.TrySetCanceled();
        }

        return completion.Task;
    }
}
