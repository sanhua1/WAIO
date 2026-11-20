using Waio.Kit.Commands;
using Waio.Kit.Input;

namespace Waio.Kit.Features;

/// <summary>功能的静态元信息。宿主不实例化功能就能读取(设置页、冲突检测、绑定编辑器都靠它)。</summary>
/// <param name="Id">全局唯一,永不改名。</param>
/// <param name="DisplayName">界面显示名,如 "剪贴板"。</param>
/// <param name="Summary">一句话说明。</param>
/// <param name="IconGlyph">Segoe Fluent Icons 的字符码,如 ""。</param>
/// <param name="RequiredCapabilities">运行所需的系统能力。</param>
/// <param name="Commands">本功能提供的全部命令。注册时若不在此列表,宿主拒绝。</param>
/// <param name="DefaultBindings">出厂快捷键,用户可改。</param>
/// <param name="EnabledByDefault">首次安装是否默认启用。</param>
public sealed record FeatureManifest(
    FeatureId Id,
    string DisplayName,
    string Summary,
    string IconGlyph,
    Capability RequiredCapabilities,
    IReadOnlyList<CommandDescriptor> Commands,
    IReadOnlyList<DefaultBinding> DefaultBindings,
    bool EnabledByDefault = true);

/// <summary>出厂绑定:某个触发器 → 本功能的某个命令。</summary>
public sealed record DefaultBinding(Trigger Trigger, CommandId Command, CommandArguments? Arguments = null);

/// <summary>功能目录里的一项:清单 + 工厂。由 App 的 FeatureCatalog 统一登记(全仓库唯一知道"所有功能"的地方)。</summary>
public sealed record FeatureDescriptor(FeatureManifest Manifest, Func<IFeatureContext, IFeature> Create);
