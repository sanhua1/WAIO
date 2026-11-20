using Microsoft.Extensions.Logging;
using Waio.Core.Storage;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Core.Host;

/// <summary>
/// 某个功能专属的 <see cref="IFeatureContext"/>:命令注册器被限制、输入服务带资源追踪、
/// 设置和存储被限定在该功能的命名空间。<see cref="DisposeAsync"/> 释放该功能申请过的全部宿主资源。
/// </summary>
internal sealed class FeatureContext : IFeatureContext, IAsyncDisposable
{
    private readonly FeatureCommandRegistrar _registrar;
    private readonly TrackingInputService _input;

    public FeatureContext(FeatureManifest manifest, HostServices services, ILoggerFactory loggerFactory)
    {
        FeatureId = manifest.Id;
        _registrar = new FeatureCommandRegistrar(manifest, services.CommandRegistrar);
        _input = new TrackingInputService(services.Input);
        CommandBus = services.CommandBus;
        Settings = services.SettingsFor(manifest.Id);
        Storage = new FeatureStorage(manifest.Id, services.Paths);
        Secrets = services.SecretsFor(manifest.Id);
        Overlay = services.Overlay;
        Events = services.Events;
        Shortcuts = services.Shortcuts;
        Launcher = services.Launcher;
        Clipboard = services.Clipboard;
        Ui = services.Ui;
        Logger = loggerFactory.CreateLogger($"feature.{manifest.Id}");
    }

    public FeatureId FeatureId { get; }

    public ICommandRegistrar Commands => _registrar;

    public ICommandBus CommandBus { get; }

    public IInputService Input => _input;

    public IFeatureSettings Settings { get; }

    public IFeatureStorage Storage { get; }

    public ISecretStore Secrets { get; }

    public IOverlayService Overlay { get; }

    public ISystemEvents Events { get; }

    public IShortcutDirectory Shortcuts { get; }

    public ILauncher Launcher { get; }

    public IClipboardService Clipboard { get; }

    public IUiThread Ui { get; }

    public ILogger Logger { get; }

    public async ValueTask DisposeAsync()
    {
        _registrar.Dispose();
        await _input.DisposeAsync().ConfigureAwait(false);
    }
}
