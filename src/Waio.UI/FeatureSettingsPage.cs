using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Waio.Kit.Commands;
using Waio.Kit.Features;
using Waio.Kit.Services;

namespace Waio.UI;

/// <summary>
/// 功能自带的设置界面(ADR-0010)。功能在自己的项目里实现,App 的 FeatureCatalog 登记,
/// 设置窗口在该功能的页面里调用 <see cref="Create"/> 摆放出来。
/// </summary>
/// <param name="Feature">属于哪个功能。</param>
/// <param name="Create">在 UI 线程上创建设置界面;每次进入该功能页面都会重新创建。</param>
public sealed record FeatureSettingsPage(FeatureId Feature, Func<FeatureSettingsContext, UIElement> Create);

/// <summary>设置界面能接触到的东西。</summary>
/// <param name="Settings">
/// 本功能的设置。与运行中的功能是<b>同一个实例</b>:保存后功能通过 <c>Changed</c> 实时生效;功能停用时也能读写。
/// </param>
/// <param name="Commands">执行需要运行中功能参与的动作(如"清空历史");功能没启用时抛 <c>CommandException</c>(Unavailable)。</param>
/// <param name="Logger">日志。</param>
/// <param name="Secrets">本功能的密钥(ADR-0014),与运行中的功能是同一个实例。</param>
/// <param name="Storage">本功能的数据目录(如翻译历史)。</param>
public sealed record FeatureSettingsContext(
    IFeatureSettings Settings, ICommandBus Commands, ILogger Logger, ISecretStore Secrets, IFeatureStorage Storage);
