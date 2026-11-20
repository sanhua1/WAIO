using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Waio.Core.Bindings;
using Waio.Core.Commands;
using Waio.Core.Host;
using Waio.Core.Input;
using Waio.Core.Launch;
using Waio.Core.Storage;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;
using Waio.UI;

namespace Waio.Shell.Hosting;

/// <summary>
/// 设置窗口等宿主 UI 使用的"应用服务"门面:持有已构造好的核心服务和当前状态,
/// 页面只通过它操作(切换功能、重载绑定、开机自启……),不直接碰 Core 的各个对象。
/// </summary>
internal sealed class ShellContext(
    AppPaths paths,
    ILogger logger,
    IReadOnlyList<FeatureManifest> manifests,
    CommandBus bus,
    InputEngine input,
    FeatureHost features,
    BindingStore bindingStore,
    AppSettingsStore appSettingsStore,
    ILauncher launcher,
    AutostartTask autostart,
    DispatcherQueue ui,
    IReadOnlyList<FeatureSettingsPage> settingsPages,
    SettingsHub settingsHub,
    ILoggerFactory loggers)
{
    public AppPaths Paths => paths;

    public IReadOnlyList<FeatureManifest> Manifests => manifests;

    public FeatureHost Features => features;

    public InputEngine Input => input;

    public DispatcherQueue Ui => ui;

    public bool IsElevated { get; } = WindowsCapabilityProbe.IsElevated();

    /// <summary>系统 UAC 是否开启。关闭时没有"普通令牌"可降,外部程序降权启动无法生效(ADR-0006)。</summary>
    public bool IsUacEnabled { get; } = UacStatus.IsEnabled();

    public AppSettings Settings { get; private set; } = new();

    public BindingLoadResult Bindings { get; private set; } = new([], []);

    public IReadOnlyList<BindingConflict> Conflicts { get; private set; } = [];

    /// <summary>绑定重新加载后触发(可能在任意线程)。</summary>
    public event Action? BindingsChanged;

    public bool IsCommandAvailable(CommandId id) => bus.IsRegistered(id);

    /// <summary>宿主自己提供的命令(ADR-0025:<c>shell.openSettings</c>)在这里注册。</summary>
    public ICommandRegistrar Commands => bus;

    public string DescribeCommand(CommandId id) =>
        manifests.SelectMany(m => m.Commands).FirstOrDefault(c => c.Id == id)?.Title ?? id.Value;

    public void LoadSettings()
    {
        Settings = appSettingsStore.Load();
        input.SetPassthroughApps(Settings.RemoteDesktop.ActiveApps());
    }

    // ---- 远程桌面让出按键(ADR-0033) ----

    private bool _remoteDesktopActive;

    /// <summary>修改远程桌面设置:写 app.json,立即生效。</summary>
    public async Task SetRemoteDesktopAsync(bool enabled, IEnumerable<string> apps)
    {
        Settings.RemoteDesktop.Enabled = enabled;
        Settings.RemoteDesktop.Apps = [.. RemoteDesktopSettings.Normalize(apps)];
        input.SetPassthroughApps(Settings.RemoteDesktop.ActiveApps());
        await appSettingsStore.SaveAsync(Settings).ConfigureAwait(true);
    }

    /// <summary>前台程序变化(前台跟踪线程上调用):远程桌面开始 / 结束时写日志,方便确认名单里的进程名对不对。</summary>
    public void OnForegroundChanged(string? processName)
    {
        var apps = Settings.RemoteDesktop.ActiveApps();
        var active = !string.IsNullOrEmpty(processName) && apps.Contains(processName, StringComparer.OrdinalIgnoreCase);
        if (active != _remoteDesktopActive)
        {
            _remoteDesktopActive = active;
            logger.LogInformation("远程桌面{State}:{Process}(快捷键和改键{Effect})",
                active ? "在前台" : "离开前台", processName, active ? "已让出" : "恢复");
        }
    }

    /// <summary>某个功能当前的启用意图(用户开关)。</summary>
    public bool IsFeatureEnabled(FeatureManifest manifest) => Settings.IsEnabled(manifest);

    /// <summary>
    /// 创建功能自带的设置界面(ADR-0010);功能没有提供则返回 null。必须在 UI 线程调用。
    /// 给界面的设置对象与运行中的功能共用(SettingsHub 每个功能一个实例),保存即生效。
    /// </summary>
    public Microsoft.UI.Xaml.UIElement? CreateFeatureSettings(FeatureId id)
    {
        var page = settingsPages.FirstOrDefault(p => p.Feature == id);
        return page?.Create(new FeatureSettingsContext(
            settingsHub.ForFeature(id), bus, loggers.CreateLogger($"settings-ui.{id}"),
            settingsHub.SecretsFor(id), new Waio.Core.Storage.FeatureStorage(id, paths)));
    }

    /// <summary>功能设置文件的路径(没有图形化设置页的功能,让用户直接编辑它)。</summary>
    public string FeatureSettingsFile(FeatureId id) => paths.FeatureSettingsFile(id);

    /// <summary>
    /// 把本次启用的功能的出厂默认绑定追加进 bindings.json(每条默认绑定只处理一次,ADR-0022)。必须在 <see cref="LoadSettings"/> 之后、
    /// <see cref="ReloadBindings"/> 之前调用。失败(如磁盘问题)只记日志:没有默认快捷键不应该让程序起不来。
    /// </summary>
    public async Task SeedDefaultBindingsAsync()
    {
        try
        {
            var enabled = manifests.Where(Settings.IsEnabled).ToList();
            var existing = bindingStore.Load().Bindings;
            var seed = DefaultBindingSeeder.Seed(enabled, Settings.SeededFeatures, Settings.SeededBindings, existing);
            if (seed.IsEmpty)
            {
                return;
            }

            foreach (var binding in seed.NewBindings)
            {
                logger.LogInformation("加上默认快捷键 {Trigger} → {Command}", binding.Trigger is HotkeyTrigger h ? h.Chord.ToString() : binding.Trigger, binding.Command);
            }

            foreach (var note in seed.Skipped)
            {
                logger.LogInformation("{Note}", note);
            }

            if (seed.NewBindings.Count > 0)
            {
                await bindingStore.SaveAsync([.. existing, .. seed.NewBindings]).ConfigureAwait(false);
            }

            Settings.SeededFeatures.AddRange(seed.SeededFeatures);
            Settings.SeededBindings.AddRange(seed.SeededBindings);
            await appSettingsStore.SaveAsync(Settings).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "写入出厂默认快捷键失败");
        }
    }

    /// <summary>读取 bindings.json 并立即生效(原子替换匹配器)。解析失败的条目只丢弃该条,错误留给设置页显示。</summary>
    public void ReloadBindings()
    {
        Bindings = bindingStore.Load();
        Conflicts = BindingConflictDetector.Detect(Bindings.Bindings.Where(b => b.Enabled));
        input.SetCustomModifiers(CustomModifiers);
        input.SetBindings(Bindings.Bindings);
        foreach (var error in Bindings.Errors)
        {
            logger.LogWarning("绑定加载:{Error}", error);
        }

        BindingsChanged?.Invoke();
    }

    // ---- 图形化编辑快捷键(ADR-0011) ----

    private readonly SemaphoreSlim _bindingWrite = new(1, 1);

    /// <summary>提供某个命令的功能;命令不存在返回 null。</summary>
    public FeatureManifest? OwnerOf(CommandId id) => manifests.FirstOrDefault(m => m.Commands.Any(c => c.Id == id));

    public CommandDescriptor? DescriptorOf(CommandId id) => manifests.SelectMany(m => m.Commands).FirstOrDefault(c => c.Id == id);

    /// <summary>保存前检查(冲突、系统快捷键、必填参数……)。</summary>
    public IReadOnlyList<BindingIssue> CheckBinding(CommandBinding binding) =>
        BindingRules.Check(binding, Bindings.Bindings, manifests, CustomModifiers);

    /// <summary>当前生效的自定义修饰键(ADR-0012,规范键名)。</summary>
    public IReadOnlyList<string> CustomModifiers => Settings.ValidCustomModifiers();

    /// <summary>修改自定义修饰键:写 app.json,立即生效,并刷新绑定状态(依赖它的绑定可能从"未生效"变为正常)。</summary>
    public async Task SetCustomModifiersAsync(IEnumerable<string> keys)
    {
        Settings.CustomModifiers = [.. keys.Where(KeyNames.CanBeCustomModifier).Select(KeyNames.Canonicalize).Distinct()];
        await appSettingsStore.SaveAsync(Settings).ConfigureAwait(true);
        ReloadBindings();
    }

    /// <summary>外观设置(预设 + 微调,ADR-0026)。</summary>
    public Waio.UI.Theming.GlassOverrides Appearance => AppearanceMapper.ToOverrides(Settings.Appearance);

    /// <summary>保存外观设置到 app.json(应用由调用方通过 <c>Glass.Apply</c> 先做,这里只负责落盘)。</summary>
    public async Task SaveAppearanceAsync(Waio.UI.Theming.GlassOverrides appearance)
    {
        Settings.Appearance = AppearanceMapper.ToSettings(appearance);
        try
        {
            await appSettingsStore.SaveAsync(Settings).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "保存外观设置失败");
        }
    }

    /// <summary>新增或修改一条绑定,写回 bindings.json 并立即生效。</summary>
    public Task SaveBindingAsync(CommandBinding binding) =>
        EditBindingsAsync(list => BindingRules.Upsert(list, binding with
        {
            Arguments = BindingRules.CleanArguments(binding.Arguments, DescriptorOf(binding.Command)),
        }));

    public Task DeleteBindingAsync(Guid id) => EditBindingsAsync(list => BindingRules.Remove(list, id));

    /// <summary>全部 Windows 默认快捷键现在归谁(ADR-0029)。</summary>
    public IReadOnlyList<SystemShortcutUsage> SystemShortcutUsage() =>
        SystemShortcutUsages.Evaluate(Bindings.Bindings, IsCommandAvailable);

    /// <summary>恢复一个系统快捷键:停用占用它的 WAIO 快捷键(不删除),钩子不再吞它,系统功能立即回来。</summary>
    public Task RestoreSystemShortcutAsync(KeyChord chord)
    {
        logger.LogInformation("恢复系统快捷键 {Chord}:停用占用它的 WAIO 快捷键", chord);
        return EditBindingsAsync(list => SystemShortcutUsages.Release(list, chord));
    }

    /// <summary>
    /// 改之前先从文件重新读一遍(不覆盖用户在文件里刚做的手工修改),改完写回并重载。
    /// 文件里无法识别的条目在加载时已被拒绝(原文件另有 .lossy 备份),写回后不再保留。
    /// </summary>
    private async Task EditBindingsAsync(Func<IReadOnlyList<CommandBinding>, List<CommandBinding>> edit)
    {
        await _bindingWrite.WaitAsync().ConfigureAwait(true);
        try
        {
            var updated = edit(bindingStore.Load().Bindings);
            await bindingStore.SaveAsync(updated).ConfigureAwait(true);
            ReloadBindings();
        }
        finally
        {
            _bindingWrite.Release();
        }
    }

    /// <summary>输入引擎在运行,快捷键生效、可以录制。</summary>
    public bool IsInputRunning => input.IsRunning;

    /// <summary>录制一个快捷键(录制期间全局按键被吞、快捷键暂停)。</summary>
    public async Task<HotkeyRecording> RecordHotkeyAsync(Action<string> hint, CancellationToken cancellationToken)
    {
        var result = await input.RecordChordAsync(hint, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            ChordRecordOutcome.Recorded => new HotkeyRecording(HotkeyRecordingOutcome.Recorded, result.Chord),
            ChordRecordOutcome.Cleared => new HotkeyRecording(HotkeyRecordingOutcome.Cleared),
            ChordRecordOutcome.Unavailable => new HotkeyRecording(HotkeyRecordingOutcome.Unavailable),
            _ => new HotkeyRecording(HotkeyRecordingOutcome.Cancelled),
        };
    }

    public async Task SetFeatureEnabledAsync(FeatureId id, bool enabled)
    {
        Settings.Features[id.Value] = enabled;
        await appSettingsStore.SaveAsync(Settings).ConfigureAwait(false);
        if (enabled)
        {
            await features.ActivateAsync(id).ConfigureAwait(false);
        }
        else
        {
            await features.DeactivateAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>保存 app.json(检查更新等宿主自己的设置改了以后)。</summary>
    public Task SaveAppSettingsAsync() => appSettingsStore.SaveAsync(Settings);

    /// <summary>检查更新(ADR-0031),第一次用到时创建。</summary>
    public UpdateCoordinator Updates => _updates ??= new UpdateCoordinator(this, loggers.CreateLogger("update"));

    private UpdateCoordinator? _updates;

    /// <summary>用 <see cref="ILauncher"/> 打开文件夹/文件:和所有外部启动一样默认降权。</summary>
    public async Task OpenAsync(string path)
    {
        try
        {
            await launcher.LaunchAsync(new LaunchRequest(path)).ConfigureAwait(false);
        }
        catch (LaunchException ex)
        {
            logger.LogWarning(ex, "打开 {Path} 失败", path);
        }
    }

    /// <returns>true = 已发起提权实例(调用方应随后退出);false = 用户拒绝了 UAC。</returns>
    public bool TryRelaunchElevated() =>
        ElevationRelauncher.TryRelaunchElevated(Environment.ProcessPath!, "--relaunched");

    public bool IsAutostartInstalled() => autostart.IsInstalled();

    /// <summary>
    /// 开关开机自启。开发调试时程序在用户可写目录,默认被拒绝(提权风险);设环境变量 WAIO_DEV=1 才放行。
    /// </summary>
    public AutostartResult SetAutostart(bool enabled) =>
        enabled
            ? autostart.Install(Environment.ProcessPath!, allowUnsafePath: Environment.GetEnvironmentVariable("WAIO_DEV") == "1")
            : autostart.Uninstall();
}

