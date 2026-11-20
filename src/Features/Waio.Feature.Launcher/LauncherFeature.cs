using Microsoft.Extensions.Logging;
using Waio.Feature.Launcher.Domain;
using Waio.Feature.Launcher.Index;
using Waio.Feature.Launcher.Infrastructure;
using Waio.Feature.Launcher.Panel;
using Waio.Feature.Launcher.UI;
using Waio.Kit.Commands;
using Waio.Kit.Contracts;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.UI;

namespace Waio.Feature.Launcher;

/// <summary>
/// 启动台(ADR-0023,对应 MAIO 的 FeatureLauncher):Ctrl+空格 弹出搜索框,搜应用(拼音、首字母,常用的排前面)、网页搜索,
/// Tab 切换来源。第一阶段(P1)只有应用和网页;文件(P2,NTFS 索引)、Everything 语法(P3)、命令(P4)见 ADR。
/// 本类只负责接线:应用目录在 STA 后台线程扫描,面板和交互流程在 UI 线程(<see cref="LauncherPresenter"/>)。
/// </summary>
public sealed partial class LauncherFeature(IFeatureContext context) : IFeature
{
    public static readonly CommandId Show = "launcher.show";
    public static readonly CommandId ShowApps = "launcher.showApps";
    public static readonly CommandId ShowFiles = "launcher.showFiles";
    public static readonly CommandId RebuildIndex = "launcher.rebuildIndex";

    /// <summary>打开面板时,应用列表超过这么久没刷新就在后台重新扫描(先用旧列表,扫完自动刷新;同 MAIO)。</summary>
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromSeconds(60);

    public static FeatureManifest Manifest { get; } = new(
        "launcher",
        "启动台",
        "Ctrl+空格 弹出搜索框:搜应用(支持拼音和首字母,常用的排前面)、全盘搜文件、网页搜索,Tab 切换来源",
        "", // Segoe Fluent Icons: Search
        Capability.None,
        [
            new CommandDescriptor(Show, "启动台"),
            new CommandDescriptor(ShowApps, "启动台(只搜应用)"),
            new CommandDescriptor(ShowFiles, "启动台(只搜文件)"),
            new CommandDescriptor(RebuildIndex, "重建文件索引"),
        ],
        [new DefaultBinding(new HotkeyTrigger(KeyChord.Parse("ctrl+space")), Show)]);

    public static FeatureDescriptor Descriptor { get; } = new(Manifest, ctx => new LauncherFeature(ctx));

    /// <summary>设置窗口里"启动台"页的设置部分(ADR-0010)。</summary>
    public static FeatureSettingsPage SettingsPage { get; } = new(Manifest.Id, LauncherSettingsView.Create);

    private LauncherSettings _settings = new();
    private UsageStore? _usageStore;
    private StaWorker? _shell;
    private IDisposable? _showRegistration;
    private IDisposable? _showAppsRegistration;
    // 扫描结果和对应的结果条目(搜索键含拼音,在后台算好);只在 UI 线程替换
    private List<(AppEntry Entry, LauncherItem Item)> _apps = [];
    private DateTimeOffset? _scannedAt;
    private bool _scanning;

    // 以下只在 UI 线程访问
    private readonly LauncherState _state = new();
    private LauncherWindow? _window;
    private LauncherPresenter? _presenter;

    public ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        _settings = context.Settings.Get<LauncherSettings>();
        _usageStore = new UsageStore(context.Storage.DataDirectory, context.Logger);
        _state.Usage = _usageStore.Load();
        _state.Settings = _settings;
        _shell = new StaWorker("launcher-shell");

        context.Settings.Changed += OnSettingsChanged;
        _showRegistration = context.Commands.Register(Show, (_, _) => Toggle(LauncherSource.All));
        _showAppsRegistration = context.Commands.Register(ShowApps, (_, _) => Toggle(LauncherSource.Apps));
        RegisterFileCommands();
        StartFileIndex();

        Rescan();
        context.Ui.Post(() => EnsurePanel()); // 预热:第一次按快捷键时不用等窗口创建
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync()
    {
        context.Settings.Changed -= OnSettingsChanged;
        _showRegistration?.Dispose();
        _showAppsRegistration?.Dispose();
        StopFileIndex();
        context.Ui.Post(() =>
        {
            _window?.Close();
            _window = null;
            _presenter = null;
        });
        _shell?.Dispose();
        _shell = null;
        return ValueTask.CompletedTask;
    }

    private ValueTask Toggle(LauncherSource source)
    {
        // 窗口只能在 UI 线程创建和操作;快捷键命令来自后台分发线程
        context.Ui.Post(() => _ = ToggleAsync(source));
        return ValueTask.CompletedTask;
    }

    private async Task ToggleAsync(LauncherSource source)
    {
        try
        {
            var presenter = EnsurePanel();
            if (!presenter.IsVisible && (_scannedAt is null || DateTimeOffset.Now - _scannedAt > CatalogMaxAge))
            {
                Rescan();
            }

            await presenter.ToggleAsync(source);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "显示启动台失败");
            await context.Overlay.ShowHudAsync("启动台打开失败,详见日志");
        }
    }

    private LauncherPresenter EnsurePanel()
    {
        if (_presenter is not null)
        {
            return _presenter;
        }

        _window = new LauncherWindow(context.Logger, LoadIconAsync);
        _presenter = new LauncherPresenter(
            _state, () => _settings, _window, new WindowsPanelSystem(), context.Launcher, context.Clipboard,
            context.Overlay, book => _usageStore?.SaveInBackground(book), context.Logger, new IndexFileSearch(() => _fileIndex),
            () => context.CommandBus.PerformAsync(ShellContracts.OpenSettings, default).AsTask());
        _window.Attach(_presenter);
        _presenter.SetApps(Items());
        _presenter.SetFileStatus(_fileStatus);
        return _presenter;
    }

    private Task<IconPixels?> LoadIconAsync(string parsingName, int size) =>
        _shell?.RunAsync(() => AppIcons.Load(parsingName, size)) ?? Task.FromResult<IconPixels?>(null);

    /// <summary>在 STA 后台线程扫描应用目录,完成后在 UI 线程替换列表。同一时间只扫一次。</summary>
    private void Rescan()
    {
        if (_scanning || _shell is not { } shell)
        {
            return;
        }

        _scanning = true;
        var started = DateTimeOffset.Now;
        var building = shell.RunAsync(AppCatalog.Scan).ContinueWith(
            scan => scan.Result.Select(e => (e, LauncherItem.App(e))).ToList(), // 拼音等搜索键在线程池上算,不占 STA 线程
            TaskScheduler.Default);
        _ = building.ContinueWith(task =>
        {
            context.Ui.Post(() =>
            {
                _scanning = false;
                if (task.IsCompletedSuccessfully)
                {
                    _apps = task.Result;
                    _scannedAt = DateTimeOffset.Now;
                    _presenter?.SetApps(Items());
                    context.Logger.LogInformation(
                        "启动台:扫描到 {Count} 个应用,用时 {Elapsed} ms(拼音表 {Han} 字)",
                        _apps.Count, (int)(DateTimeOffset.Now - started).TotalMilliseconds, Pinyin.Count);
                }
                else if (task.Exception is { } ex)
                {
                    context.Logger.LogError(ex.GetBaseException(), "启动台扫描应用失败");
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>扫描结果 → 结果条目(按设置过滤卸载程序;设置改了不用重扫)。</summary>
    private List<LauncherItem> Items() =>
        _apps.Where(a => !_settings.HideUninstallers || !AppCatalog.IsUninstaller(a.Entry)).Select(a => a.Item).ToList();

    private void OnSettingsChanged()
    {
        _settings = context.Settings.Get<LauncherSettings>();
        ApplyFileSettings(_settings);
        context.Ui.Post(() =>
        {
            _presenter?.SetApps(Items());
            _presenter?.ApplySettings(_settings);
        });
    }
}
