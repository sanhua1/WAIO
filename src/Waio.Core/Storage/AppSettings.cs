using Microsoft.Extensions.Logging;
using Waio.Kit.Features;

namespace Waio.Core.Storage;

/// <summary>
/// app.json:全局设置。<see cref="Features"/> 只记录用户<b>明确做过的</b>开关选择,
/// 没记录的功能按清单里的 <see cref="FeatureManifest.EnabledByDefault"/> 决定——
/// 这样以后新增的默认启用功能,老用户升级后也会自动启用。
/// 字段只增不删(ARCHITECTURE §7)。
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>功能 id → 是否启用。</summary>
    public Dictionary<string, bool> Features { get; set; } = [];

    /// <summary>已经播种过的功能 id(见 DefaultBindingSeeder)。用来区分"功能第一次启用"和"功能后来新增的默认绑定"。</summary>
    public List<string> SeededFeatures { get; set; } = [];

    /// <summary>已经处理过的每一条出厂默认绑定(<c>DefaultBindingSeeder.KeyOf</c>,如 "screenshot.record|alt+f3";ADR-0022)。</summary>
    public List<string> SeededBindings { get; set; } = [];

    /// <summary>
    /// 自定义修饰键(ADR-0012),如 ["f24"]:按住它再按别的键组成快捷键("f24+j"),单独轻点仍是它本身。
    /// 配置是不可信输入,读取时用 <see cref="ValidCustomModifiers"/> 过滤。
    /// </summary>
    public List<string> CustomModifiers { get; set; } = [];

    /// <summary>外观(磨砂玻璃主题,ADR-0026)。全部为空 = 默认预设"雾面"。</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>检查更新(ADR-0031)。</summary>
    public UpdateSettings Update { get; set; } = new();

    /// <summary>远程桌面在前台时让出按键(ADR-0033)。</summary>
    public RemoteDesktopSettings RemoteDesktop { get; set; } = new();

    /// <summary>
    /// 合法、去重、规范化后的自定义修饰键。只接受 F1–F24、CapsLock 等不用来打字的键——
    /// 手改 app.json 把字母设成修饰键会让打字变得别扭,直接忽略。
    /// </summary>
    public IReadOnlyList<string> ValidCustomModifiers() =>
        [.. (CustomModifiers ?? []).Where(Input.KeyNames.CanBeCustomModifier).Select(Input.KeyNames.Canonicalize).Distinct()];

    /// <summary>
    /// 要启用的功能。配置是不可信输入:不在目录里的 id 一律忽略。
    /// 顺序与目录一致,使激活顺序稳定。
    /// </summary>
    public IReadOnlyList<FeatureId> ResolveEnabled(IEnumerable<FeatureManifest> catalog) =>
        [.. catalog
            .Where(m => IsEnabled(m))
            .Select(m => m.Id)];

    public bool IsEnabled(FeatureManifest manifest) =>
        Features is not null && Features.TryGetValue(manifest.Id.Value, out var chosen) ? chosen : manifest.EnabledByDefault;
}

public sealed class AppSettingsStore(AppPaths paths, ILogger logger)
{
    /// <summary>读取 app.json;不存在或损坏(已备份)时返回默认值。</summary>
    public AppSettings Load()
    {
        var settings = SafeJsonFile.TryRead<AppSettings>(paths.AppSettingsFile, logger, out _) ?? new AppSettings();
        settings.Features ??= []; // JSON 里写 "features": null 时,不能留下空引用
        settings.SeededFeatures ??= [];
        settings.SeededBindings ??= [];
        settings.CustomModifiers ??= [];
        settings.Appearance ??= new AppearanceSettings();
        settings.Update ??= new UpdateSettings();
        settings.RemoteDesktop ??= new RemoteDesktopSettings();
        settings.RemoteDesktop.Apps ??= [];
        return settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        SafeJsonFile.WriteAtomicAsync(paths.AppSettingsFile, SafeJsonFile.Serialize(settings), cancellationToken);
}

