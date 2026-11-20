using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Waio.Core.Bindings;
using Waio.Core.Clipboard;
using Waio.Core.Commands;
using Waio.Core.Host;
using Waio.Core.Input;
using Waio.Core.Launch;
using Waio.Core.Logging;
using Waio.Core.Platform;
using Waio.Core.Storage;
using Waio.Kit.Commands;
using Waio.Kit.Contracts;
using Waio.Kit.Features;
using Waio.UI;
using Waio.Shell.Overlay;
using Waio.Shell.Settings;
using Waio.Shell.Tray;

namespace Waio.Shell.Hosting;

/// <summary>
/// 组合根:把 Core 的各个服务接起来(命令总线、输入引擎、功能宿主、日志……),并拥有托盘和设置窗口。
/// 功能清单由 App 传入——Shell 不引用任何具体功能(ARCHITECTURE §2)。
/// 必须在 UI 线程创建和启动。
/// </summary>
public sealed class ShellHost : IAsyncDisposable
{
    private readonly ShellContext _context;
    private readonly ForegroundTracker _foreground;
    private readonly HudOverlay _hud;
    private readonly ClipboardService _clipboard;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly DispatcherQueue _ui;

    private TrayIcon? _tray;
    private IDisposable? _settingsCommand;
    private SettingsWindow? _settings;

    private ShellHost(
        ShellContext context, ForegroundTracker foreground, HudOverlay hud, ClipboardService clipboard,
        ILoggerFactory loggers, DispatcherQueue ui)
    {
        _context = context;
        _foreground = foreground;
        _hud = hud;
        _clipboard = clipboard;
        _loggers = loggers;
        _logger = loggers.CreateLogger("shell");
        _ui = ui;
    }

    /// <summary>用户在托盘点了"退出"(或提权重启后旧实例需要退出)。</summary>
    public event Action? ExitRequested;

    public static ShellHost Create(
        IReadOnlyList<FeatureDescriptor> catalog, IReadOnlyList<FeatureSettingsPage> settingsPages, DispatcherQueue ui)
    {
        var paths = AppPaths.ForCurrentUser();
        var loggers = new AppLoggerFactory(new FileLoggerProvider(paths.LogsDirectory));
        var logger = loggers.CreateLogger("shell");

        var bus = new CommandBus();
        var foreground = new ForegroundTracker(loggers.CreateLogger("foreground"));
        var input = new InputEngine(bus, loggers.CreateLogger("input"), () => foreground.Foreground?.ProcessName);
        foreground.ForegroundChanged += _ => input.ReassertHookPriority(); // 切到别的程序时它可能刚装了自己的钩子
        var launcher = new ShellLauncher(loggers.CreateLogger("launcher"));
        var hud = new HudOverlay(ui);
        var hub = new SettingsHub(paths, loggers);
        var clipboard = new ClipboardService(loggers.CreateLogger("clipboard"));

        var services = new HostServices(
            bus, bus, input, hub.ForFeature, hub.SecretsFor, paths, hud, foreground, new ShortcutDirectory(), launcher,
            clipboard, new DispatcherUiThread(ui));
        var features = new FeatureHost(catalog, services, new WindowsCapabilityProbe(), loggers);

        var context = new ShellContext(
            paths, logger, [.. catalog.Select(d => d.Manifest)], bus, input, features,
            new BindingStore(paths, loggers.CreateLogger("bindings")),
            new AppSettingsStore(paths, logger),
            launcher, new AutostartTask(logger), ui, settingsPages, hub, loggers);
        foreground.ForegroundChanged += info => context.OnForegroundChanged(info.ProcessName);
        return new ShellHost(context, foreground, hud, clipboard, loggers, ui);
    }

    /// <summary>启动顺序遵循 ARCHITECTURE §8:托盘 → 功能 → 绑定 → 输入引擎。任何一步失败都不应让整个程序起不来。</summary>
    /// <param name="startInputEngine">false = 不装键盘钩子(<c>--no-hook</c>),只跑托盘和界面。</param>
    public async Task StartAsync(bool startInputEngine = true)
    {
        // 托盘必须在 UI 线程创建(此处尚未 await),并且先出现,用户立刻能看到程序在运行
        _tray = new TrayIcon(
            _context.IsElevated ? "WAIO(管理员)" : "WAIO(未以管理员身份运行:管理员窗口里快捷键无效)", _context.IsElevated);
        _tray.OpenSettingsRequested += ShowSettings;
        _tray.ExitRequested += () => ExitRequested?.Invoke();
        _tray.RelaunchElevatedRequested += OnRelaunchElevatedRequested;
        _tray.CheckUpdateRequested += () => _ = CheckUpdateFromTrayAsync();
        _tray.BalloonClicked += () => ShowSettings("general");

        // ADR-0025:功能(启动台的齿轮)请求直接打开设置窗口。ShowSettings 自己会切到 UI 线程,命令立即返回
        _settingsCommand = _context.Commands.Register(ShellContracts.OpenSettings, (_, _) =>
        {
            ShowSettings(); // 自己会切到 UI 线程
            return ValueTask.FromResult(default(Unit));
        });

        _foreground.Start();
        try
        {
            _clipboard.Start(); // 在功能启动之前:功能激活时就要能订阅 Changed
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "剪贴板监听启动失败,依赖剪贴板的功能将无法记录");
        }

        _context.LoadSettings();

        // 外观要在功能启动之前:剪贴板、启动台等面板在功能激活时就预先创建好了(ADR-0026)
        Waio.UI.Theming.Glass.Initialize(_context.Appearance.Resolve(), _logger);
        await _context.Features.StartAsync(_context.Settings.ResolveEnabled(_context.Manifests)).ConfigureAwait(true);
        await _context.SeedDefaultBindingsAsync().ConfigureAwait(true);
        _context.ReloadBindings();

        if (startInputEngine)
        {
            try
            {
                _context.Input.Start();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "输入引擎启动失败,快捷键不可用");
                await _hud.ShowHudAsync("快捷键服务启动失败,快捷键暂不可用").ConfigureAwait(true);
            }
        }
        else
        {
            _logger.LogWarning("以 --no-hook 启动:未安装键盘钩子,快捷键不可用");
        }

        _context.Updates.UpdateAvailable += release => _ui.TryEnqueue(() => _tray?.ShowBalloon(
            $"WAIO {Waio.Core.Update.ReleaseFeed.Format(release.Version)} 可以更新了", "点这里打开设置查看更新说明并安装"));
        _context.Updates.Start();

        _logger.LogInformation("WAIO 已启动(管理员={Elevated})", _context.IsElevated);
    }

    /// <summary>托盘菜单"检查更新":结果用系统通知告诉用户,有新版本时点通知打开设置。</summary>
    private async Task CheckUpdateFromTrayAsync()
    {
        var result = await _context.Updates.CheckNowAsync().ConfigureAwait(false);
        var text = result.Status switch
        {
            Waio.Core.Update.UpdateStatus.Available => "点这里打开设置查看更新说明并安装",
            Waio.Core.Update.UpdateStatus.UpToDate => $"当前版本 {_context.Updates.CurrentVersion}",
            _ => result.Message,
        };
        _ui.TryEnqueue(() => _tray?.ShowBalloon(result.Status == Waio.Core.Update.UpdateStatus.UpToDate ? "已是最新版本" : result.Message, text));
    }

    /// <summary>显示设置窗口(已打开则置前)。可从任意线程调用。</summary>
    public void ShowSettings() => ShowSettings(null);

    /// <summary>显示设置窗口并切到 <paramref name="page"/>(如 "general")。可从任意线程调用。</summary>
    public void ShowSettings(string? page)
    {
        if (!_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(() => ShowSettings(page));
            return;
        }

        if (_settings is null)
        {
            _settings = new SettingsWindow(_context);
            _settings.Closed += (_, _) => _settings = null; // 设置窗口按需创建,关闭即销毁(ARCHITECTURE §6)
        }

        _settings.Activate();
        if (page is not null)
        {
            _settings.Show(page);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _settingsCommand?.Dispose();
        _context.Updates.Dispose();
        _tray?.Dispose();
        _settings?.Close();
        await _context.Input.DisposeAsync().ConfigureAwait(false);
        await _context.Features.DisposeAsync().ConfigureAwait(false);
        _clipboard.Dispose();
        _foreground.Dispose();
        _hud.Close();
        _logger.LogInformation("WAIO 已退出");
        _loggers.Dispose();
    }

    private void OnRelaunchElevatedRequested()
    {
        if (_context.TryRelaunchElevated())
        {
            ExitRequested?.Invoke(); // 提权的新实例会等本实例释放单实例守卫(见 App 的 --relaunched 处理)
        }
        else
        {
            _ = _hud.ShowHudAsync("已取消以管理员身份重新启动");
        }
    }
}


