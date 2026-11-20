using Microsoft.Extensions.Logging;
using Waio.Feature.KeyMapping.Domain;
using Waio.Feature.KeyMapping.Realtime;
using Waio.Feature.KeyMapping.Views;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.UI;

namespace Waio.Feature.KeyMapping;

/// <summary>
/// 按键映射:改键、双功能键(ADR-0008)、Super Caps 层(ADR-0013)。规则来自 features/keymap.json,保存后立即生效。
/// 判定逻辑都在 <see cref="RemapFilter"/>(钩子线程)和 <see cref="RuleCompiler"/>(纯逻辑)里,本类只负责接线。
/// </summary>
public sealed class KeyMappingFeature(IFeatureContext context) : IFeature
{
    public static readonly CommandId Toggle = "keymap.toggle";

    public static FeatureManifest Manifest { get; } = new(
        "keymap",
        "按键映射",
        """
        让键盘按你的习惯工作:
        Super Caps:按住 CapsLock,键盘上的键临时变成方向键、翻页等第二套功能
        改键与双功能键:把一个键换成另一个键,或者让它轻点和按住时各是一个键
        """,
        "\uE765", // Segoe Fluent Icons: Keyboard
        Capability.InputHooks,
        [new CommandDescriptor(Toggle, "暂停/恢复按键映射")],
        []);

    public static FeatureDescriptor Descriptor { get; } = new(Manifest, ctx => new KeyMappingFeature(ctx));

    /// <summary>设置窗口里的页面(ADR-0010)。</summary>
    public static FeatureSettingsPage SettingsPage { get; } = new(Manifest.Id, KeyMappingSettingsView.Create);

    private readonly RemapFilter _filter = new();
    private IDisposable? _filterToken;
    private IDisposable? _toggleRegistration;
    private RuleSet _compiled = RuleSet.Empty;
    private volatile bool _paused;

    public ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        _filterToken = context.Input.RegisterKeyFilter(_filter);
        _toggleRegistration = context.Commands.Register(Toggle, ToggleAsync);
        context.Settings.Changed += Reload;
        Reload();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync()
    {
        context.Settings.Changed -= Reload;
        _filter.Publish(RuleSet.Empty); // 先清空规则:下一个按键事件上会释放卡住的 hold 键
        _filterToken?.Dispose();
        _toggleRegistration?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Reload()
    {
        var result = RuleCompiler.Compile(context.Settings.Get<KeyMappingSettings>(), name => context.Input.NormalizeKey(name));
        foreach (var error in result.Errors)
        {
            context.Logger.LogWarning("按键映射配置:{Error}", error);
        }

        _compiled = result.Rules;
        Publish();
        context.Logger.LogInformation(
            "按键映射已加载 {Count} 条规则;Super Caps {SuperCaps}",
            result.Rules.Count,
            result.Rules.Layer is { } layer ? $"已启用({layer.Map.Count} 个映射)" : "未启用");
    }

    private void Publish()
    {
        var rules = _paused ? RuleSet.Empty : _compiled;
        _filter.Publish(rules);
        if (rules.Layer is not null)
        {
            TurnOffCapsLock();
        }
    }

    /// <summary>CapsLock 被吞掉以后,用户再也无法自己关掉大写锁定:启用时它若开着,先替用户关掉。</summary>
    private void TurnOffCapsLock()
    {
        try
        {
            context.Input.SetLockState(RuleCompiler.SuperCapsKey, on: false);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "Super Caps:关闭大写锁定失败");
        }
    }

    private async ValueTask ToggleAsync(CommandArguments arguments, CancellationToken cancellationToken)
    {
        _paused = !_paused;
        Publish();
        await context.Overlay.ShowHudAsync(_paused ? "按键映射:已暂停" : "按键映射:已恢复").ConfigureAwait(false);
    }
}
