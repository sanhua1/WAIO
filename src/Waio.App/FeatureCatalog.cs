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
/// 列表顺序就是激活顺序。
/// </summary>
internal static class FeatureCatalog
{
    public static IReadOnlyList<FeatureDescriptor> All { get; } =
    [
        SystemActionsFeature.Descriptor,
        KeyMappingFeature.Descriptor,
        ClipboardFeature.Descriptor,
        TranslateFeature.Descriptor,
        ScreenshotFeature.Descriptor,
        LauncherFeature.Descriptor,
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

