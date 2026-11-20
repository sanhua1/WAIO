using Waio.Core.Storage;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Core.Host;

/// <summary>
/// 宿主持有的、会按功能裁剪后交给功能的共享服务。App 在组合根里创建并注入,功能永远拿不到这个类型本身。
/// </summary>
public sealed record HostServices(
    ICommandRegistrar CommandRegistrar,
    ICommandBus CommandBus,
    IInputService Input,
    Func<FeatureId, IFeatureSettings> SettingsFor,
    Func<FeatureId, ISecretStore> SecretsFor,
    AppPaths Paths,
    IOverlayService Overlay,
    ISystemEvents Events,
    IShortcutDirectory Shortcuts,
    ILauncher Launcher,
    IClipboardService Clipboard,
    IUiThread Ui);
