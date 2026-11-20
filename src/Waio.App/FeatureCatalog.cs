using Waio.Feature.Clipboard;
using Waio.Feature.KeyMapping;
using Waio.Feature.Launcher;
using Waio.Feature.SystemActions;
using Waio.Feature.Screenshot;
using Waio.Feature.Translate;
using Waio.Kit.Features;
using Waio.UI;

namespace Waio.App;

/// <summary>
/// 全仓库<b>唯一</b>知道"所有功能"的地方(ARCHITECTURE §2)。
/// 新增一个功能 = 新增一个项目 + 在这里加一行,不改任何已有功能的代码。
/// 列表顺序就是激活顺序,也是设置窗口左栏和"WAIO 快捷键"页分组的顺序(按常用程度排)。
/// 目前只有按键映射注册实时过滤器,顺序不影响按键处理;以后再有功能注册过滤器时要考虑串联顺序。
/// </summary>
internal static class FeatureCatalog
{
    public static IReadOnlyList<FeatureDescriptor> All { get; } =
    [
        LauncherFeature.Descriptor,
        ClipboardFeature.Descriptor,
        TranslateFeature.Descriptor,
        ScreenshotFeature.Descriptor,
        KeyMappingFeature.Descriptor,
        SystemActionsFeature.Descriptor,
    ];

    /// <summary>
    /// 各功能自带的设置界面(ADR-0010)。没有登记的功能,设置窗口里只显示开关、状态、快捷键和"打开设置文件"。
    /// </summary>
    public static IReadOnlyList<FeatureSettingsPage> SettingsPages { get; } =
    [
        KeyMappingFeature.SettingsPage,
        ClipboardFeature.SettingsPage,
        TranslateFeature.SettingsPage,
        ScreenshotFeature.SettingsPage,
        LauncherFeature.SettingsPage,
    ];
}

