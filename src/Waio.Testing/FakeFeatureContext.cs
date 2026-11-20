using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Testing;

/// <summary>
/// 把所有假实现装配成一个 <see cref="IFeatureContext"/>。功能单测的标准入口:
/// <code>
/// var ctx = new FakeFeatureContext("clipboard");
/// var feature = new ClipboardFeature(ctx);
/// await feature.ActivateAsync(default);
/// await ctx.Bus.PerformAsync("clipboard.showPanel", CommandArguments.Empty);
/// </code>
/// 各个假服务以属性暴露,便于断言和驱动。
/// </summary>
public sealed class FakeFeatureContext : IFeatureContext
{
    public FakeFeatureContext(FeatureId featureId, string? dataDirectory = null)
    {
        FeatureId = featureId;
        FakeStorage = new FakeFeatureStorage(dataDirectory ?? Path.Combine(Path.GetTempPath(), "waio-tests", featureId.Value));
    }

    public FeatureId FeatureId { get; }

    public FakeCommandBus Bus { get; } = new();

    public FakeInputService FakeInput { get; } = new();

    public InMemoryFeatureSettings FakeSettings { get; } = new();

    public FakeFeatureStorage FakeStorage { get; }

    public InMemorySecretStore FakeSecrets { get; } = new();

    public RecordingOverlay FakeOverlay { get; } = new();

    public FakeSystemEvents FakeEvents { get; } = new();

    public FakeShortcutDirectory FakeShortcuts { get; } = new();

    public RecordingLauncher FakeLauncher { get; } = new();

    public FakeClipboard FakeClipboard { get; } = new();

    public ICommandRegistrar Commands => Bus;

    public ICommandBus CommandBus => Bus;

    public IInputService Input => FakeInput;

    public IFeatureSettings Settings => FakeSettings;

    public IFeatureStorage Storage => FakeStorage;

    public ISecretStore Secrets => FakeSecrets;

    public IOverlayService Overlay => FakeOverlay;

    public ISystemEvents Events => FakeEvents;

    public IShortcutDirectory Shortcuts => FakeShortcuts;

    public ILauncher Launcher => FakeLauncher;

    public IClipboardService Clipboard => FakeClipboard;

    public IUiThread Ui { get; } = new ImmediateUiThread();

    public ILogger Logger { get; } = NullLogger.Instance;
}
