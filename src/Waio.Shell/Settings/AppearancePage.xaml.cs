using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Waio.Shell.Hosting;
using Waio.UI;
using Waio.UI.Theming;

namespace Waio.Shell.Settings;

/// <summary>
/// "外观"设置页(ADR-0027):没有预设,在默认值之上直接调玻璃本身;深浅色跟随系统。
/// 每个改动立即 <see cref="Glass.Apply"/>(所有窗口实时重画),停手 400 ms 后写入 app.json。
/// </summary>
internal sealed partial class AppearancePage : Page
{

    private ShellContext _context = null!;
    private GlassOverrides _overrides = new();
    private DispatcherQueueTimer? _saveTimer;
    private bool _dirty;

    public AppearancePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => FlushSave();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _context = (ShellContext)e.Parameter;
        _overrides = _context.Appearance;
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => FlushSave();
        Build();
    }

    /// <summary>按当前设置重建整页(只在恢复默认时调用;调整时只改对应控件,不重建)。</summary>
    private void Build()
    {
        var t = _overrides.Resolve();
        Body.Content = SettingsUi.Stack(
            SettingsUi.Section("玻璃"),
            SettingsUi.Card("模糊强度", "越大背后越朦胧;太小时背后的文字会透出来",
                Slider(t.BlurRadius, GlassTheme.MinBlurRadius, GlassTheme.MaxBlurRadius, "{0:0} px", 1,
                    x => Update(o => o with { BlurRadius = x }))),
            SettingsUi.Card("鲜艳度", "让透出来的颜色更鲜艳。100% 为原样",
                Slider(t.Saturation, GlassTheme.MinSaturation, GlassTheme.MaxSaturation, "{0:0}%", 100,
                    x => Update(o => o with { Saturation = x }))),
            SettingsUi.Card("色调", "罩在玻璃上的颜色。默认随系统深浅色变化",
                ColorSetting(_overrides.Tint, "默认", () => Glass.Current.TintFor(Glass.IsDark()),
                    x => Update(o => o with { Tint = x }))),
            SettingsUi.Card("色调浓度", "越高越接近纯色、字越清楚;越低越通透",
                Slider(t.TintOpacity, 0, GlassTheme.MaxTintOpacity, "{0:0}%", 100,
                    x => Update(o => o with { TintOpacity = x }))),
            SettingsUi.Section("层次与颜色"),
            SettingsUi.Card("卡片底色", "设置分组、列表等浮在玻璃上的那一层。默认白色",
                ColorSetting(_overrides.Surface, "默认", () => Glass.Current.Surface ?? Microsoft.UI.Colors.White,
                    x => Update(o => o with { Surface = x }))),
            SettingsUi.Card("卡片浓度", "越高卡片越实,和背景的层次越分明",
                Slider(t.SurfaceOpacity, 0, 1, "{0:0}%", 100, x => Update(o => o with { SurfaceOpacity = x }))),
            SettingsUi.Card("强调色", "开关、选中项、按钮高亮的颜色",
                ColorSetting(_overrides.Accent, "跟随系统", SystemOrCustomAccent,
                    x => Update(o => o with { Accent = x }))),
            SettingsUi.Card("恢复默认", "清除所有调整",
                SettingsUi.Button("恢复", ResetAll)));
    }

    private void ResetAll()
    {
        _overrides = new GlassOverrides();
        Glass.Apply(_overrides.Resolve());
        ScheduleSave();
        Build();
    }

    /// <summary>调整一项:立即生效,稍后保存。</summary>
    private void Update(Func<GlassOverrides, GlassOverrides> change)
    {
        _overrides = change(_overrides);
        Glass.Apply(_overrides.Resolve());
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _dirty = true;
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void FlushSave()
    {
        _saveTimer?.Stop();
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _ = _context.SaveAppearanceAsync(_overrides);
    }

    private static Windows.UI.Color SystemOrCustomAccent() =>
        Glass.Current.Accent ?? new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
}
