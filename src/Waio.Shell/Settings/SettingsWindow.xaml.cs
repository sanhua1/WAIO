using Microsoft.UI.Xaml.Controls;
using Waio.Kit.Features;
using Waio.Shell.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Waio.UI;
using Waio.UI.Theming;

namespace Waio.Shell.Settings;

/// <summary>统一设置窗口(磨砂玻璃,外观可调,ADR-0026)。按需创建,关闭即销毁。</summary>
internal sealed partial class SettingsWindow : Microsoft.UI.Xaml.Window
{
    private readonly ShellContext _context;

    public SettingsWindow(ShellContext context)
    {
        _context = context;
        InitializeComponent();
        Glass.Attach(this);
        // 导航栏 248 + 页面内容最宽 920 + 边距,高度能放下一整页常见设置;小屏幕上限制在工作区的 90%
        WindowChrome.CenterOnCurrentMonitor(this, widthDip: 1240, heightDip: 880);
        if (AppIcon.Exists)
        {
            AppWindow.SetIcon(AppIcon.Path); // 任务栏、Alt+Tab 里的图标
            TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(AppIcon.Path));
        }

        // 自绘标题栏:内容延伸进标题栏,AppTitleBar 那一行作为拖动区;系统的三个按钮背景透明,和玻璃融为一体
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        UpdateCaptionButtons();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();

        GlassShape.Card(Sidebar);
        Glass.UseOwnedResources(Navigation); // 选中项用强调色淡胶囊、没有竖条,导航栏和内容区透明

        // 新页面里的按钮、输入框按当前圆角设置调整(运行中改过圆角时,新建的控件不一定拿到新值)
        ContentFrame.Navigated += (_, e) =>
        {
            if (e.Content is Microsoft.UI.Xaml.FrameworkElement page)
            {
                page.Loaded += (_, _) => GlassShape.Conform(page);
            }
        };

        // "功能"分组:每个功能一项,图标和名字取自 manifest
        foreach (var manifest in context.Manifests)
        {
            Navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = manifest.DisplayName,
                Tag = manifest,
                Icon = new FontIcon { Glyph = manifest.IconGlyph },
            });
        }

        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    /// <summary>切到某个页面:"general"、"bindings" 或功能 id。</summary>
    public void Show(string page)
    {
        var target = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i =>
            i.Tag is string tag ? tag == page : i.Tag is FeatureManifest m && m.Id.Value == page);
        if (target is not null)
        {
            Navigation.SelectedItem = target;
        }
    }

    /// <summary>
    /// 切页不要动画(切换时不要位移 / 闪烁)。Frame 默认每次导航让新页面从下往上滑入,
    /// 看起来就是"内容错位了一下";这里关掉导航过渡,内容原地替换。
    /// </summary>
    private static readonly SuppressNavigationTransitionInfo NoTransition = new();

    private object? _currentTag;

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag;
        if (tag is null || Equals(tag, _currentTag))
        {
            return; // 点已经选中的那一项:不重建页面(否则会闪一下、滚动位置回到顶部)
        }

        Navigate(tag);
    }

    private void Navigate(object tag)
    {
        _currentTag = tag;
        switch (tag)
        {
            case "bindings":
                ContentFrame.Navigate(typeof(BindingsPage), _context, NoTransition);
                break;
            case "system-shortcuts":
                ContentFrame.Navigate(typeof(SystemShortcutsPage), _context, NoTransition);
                break;
            case "appearance":
                ContentFrame.Navigate(typeof(AppearancePage), _context, NoTransition);
                break;
            case "general":
                ContentFrame.Navigate(typeof(GeneralPage), _context, NoTransition);
                break;
            case FeatureManifest manifest:
                ContentFrame.Navigate(typeof(FeaturePage), new FeaturePageArgs(_context, manifest, () => Show("bindings")), NoTransition);
                break;
        }
    }

    /// <summary>
    /// 系统画的最小化 / 最大化 / 关闭按钮:字形颜色跟着窗口实际深浅色走
    /// (外观固定为深色而系统是浅色时,系统默认会画成黑色,看不见)。
    /// </summary>
    private void UpdateCaptionButtons()
    {
        var dark = Root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0A, 0, 0, 0);
    }
}
