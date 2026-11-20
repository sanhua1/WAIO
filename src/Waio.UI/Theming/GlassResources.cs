using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Waio.UI.Theming;

/// <summary>
/// 把 <see cref="GlassPalette"/> 算出的颜色写进 XAML 资源(只在 UI 线程调用;ADR-0026)。
/// 改的是画刷对象的 <c>Color</c>,用到它的控件立即重绘,不需要重新加载页面——拖动滑块时实时预览就是这样做到的。
/// </summary>
internal static class GlassResources
{
    /// <summary>深色主题字典的键:Fluent 用 "Default" 表示深色。</summary>
    private static readonly (string Key, bool Dark)[] Themes = [("Light", false), ("Default", true), ("Dark", true)];

    /// <summary>找到的 Fluent 原画刷:(主题键, 资源键) → 画刷。</summary>
    private static readonly Dictionary<(string Theme, string Key), SolidColorBrush> Targets = [];

    /// <summary>改之前的原色,用于"跟随系统强调色"时恢复。</summary>
    private static readonly Dictionary<(string Theme, string Key), Color> Originals = [];

    /// <summary>WAIO 自己的画刷(应用级主题字典里)。</summary>
    private static readonly Dictionary<(string Theme, string Key), SolidColorBrush> Owned = [];

    private static bool _located;

    /// <summary>写入全部颜色和圆角。第一次调用必须在任何窗口创建之前(圆角和自有画刷只对之后加载的元素生效)。</summary>
    public static void Apply(GlassTheme theme, Color systemAccent, ILogger? logger)
    {
        var app = Application.Current.Resources;
        if (!_located)
        {
            _located = true;
            Locate(app, logger);
        }

        foreach (var (themeKey, dark) in Themes)
        {
            var patched = GlassPalette.Patched(theme, dark);
            foreach (var key in GlassPalette.AccentKeys.Concat(patched.Keys).Distinct())
            {
                if (!Targets.TryGetValue((themeKey, key), out var brush))
                {
                    continue;
                }

                brush.Color = patched.TryGetValue(key, out var color) ? color
                    : Originals.TryGetValue((themeKey, key), out var original) ? original
                    : brush.Color;
            }

            var owned = OwnedDictionary(app, themeKey);
            foreach (var (key, color) in GlassPalette.Owned(theme, dark, theme.Accent ?? systemAccent))
            {
                if (!Owned.TryGetValue((themeKey, key), out var brush))
                {
                    brush = new SolidColorBrush(color);
                    Owned[(themeKey, key)] = brush;
                    owned[key] = brush;
                }

                brush.Color = color;
            }
        }

        // 圆角:应用级的值优先于 Fluent 默认值,但只对之后创建的元素生效(CornerRadius 是值类型,不能就地改)
        app["ControlCornerRadius"] = new CornerRadius(theme.ControlRadius);
        app["OverlayCornerRadius"] = new CornerRadius(theme.CardRadius);
        app["WaioCardCornerRadius"] = new CornerRadius(theme.CardRadius);
        app["WaioControlCornerRadius"] = new CornerRadius(theme.ControlRadius);
    }

    /// <summary>
    /// 在 XamlControlsResources 里找到 Fluent 原画刷并记下原色。找不到的键退而在应用级字典放自己的画刷
    /// (只对模板里用 ThemeResource 引用的地方有效,别名不会跟着变),并记日志,便于排查"某处颜色没变"。
    /// </summary>
    private static void Locate(ResourceDictionary app, ILogger? logger)
    {
        var keys = GlassPalette.AccentKeys
            .Concat(GlassPalette.Patched(GlassTheme.Default, dark: false).Keys)
            .Distinct()
            .ToList();
        var missing = new List<string>();
        foreach (var (themeKey, _) in Themes)
        {
            var dictionaries = ThemeDictionaries(app, themeKey).ToList();
            if (dictionaries.Count == 0)
            {
                continue; // "Dark" 在 Fluent 里通常不存在(深色用 "Default"),没有就跳过
            }

            foreach (var key in keys)
            {
                var brush = dictionaries
                    .Select(d => d.TryGetValue(key, out var value) ? value as SolidColorBrush : null)
                    .FirstOrDefault(b => b is not null);
                if (brush is null)
                {
                    missing.Add($"{themeKey}/{key}");
                    continue;
                }

                Targets[(themeKey, key)] = brush;
                Originals[(themeKey, key)] = brush.Color;
            }
        }

        logger?.LogInformation("外观:找到 {Found} 个 Fluent 画刷可直接改色;找不到 {Missing} 个{List}",
            Targets.Count, missing.Count, missing.Count == 0 ? "" : ":" + string.Join(", ", missing));
    }

    /// <summary>
    /// 把 WAIO 自有画刷(导航栏透明、选中胶囊等)也放进某个元素自己的主题字典。
    /// 实测(2026-11-12):运行时加到应用级主题字典里的导航栏键,NavigationView 模板不认(选中项仍有竖条、灰底),
    /// 放在元素自己的资源里最可靠。放的是同一批画刷对象,外观改动时颜色照样实时变。
    /// </summary>
    public static void AttachOwned(FrameworkElement element)
    {
        foreach (var (themeKey, _) in Themes)
        {
            var dictionary = new ResourceDictionary();
            foreach (var ((theme, key), brush) in Owned)
            {
                if (theme == themeKey)
                {
                    dictionary[key] = brush;
                }
            }

            if (dictionary.Count > 0)
            {
                element.Resources.ThemeDictionaries[themeKey] = dictionary;
            }
        }
    }
    /// <summary>应用自己的主题字典(没有就建一个),用于放 WAIO 自有画刷。</summary>
    private static ResourceDictionary OwnedDictionary(ResourceDictionary app, string themeKey)
    {
        if (app.ThemeDictionaries.TryGetValue(themeKey, out var existing) && existing is ResourceDictionary dictionary)
        {
            return dictionary;
        }

        dictionary = new ResourceDictionary();
        app.ThemeDictionaries[themeKey] = dictionary;
        return dictionary;
    }

    /// <summary>某个主题键下的所有主题字典,应用自己的除外(那里放的是 WAIO 自有画刷),按查找顺序递归合并字典。</summary>
    private static IEnumerable<ResourceDictionary> ThemeDictionaries(ResourceDictionary root, string themeKey)
    {
        // 合并字典后加入的优先,和 XAML 的查找顺序一致
        for (var i = root.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var merged = root.MergedDictionaries[i];
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var value) && value is ResourceDictionary own)
            {
                yield return own;
            }

            foreach (var nested in ThemeDictionaries(merged, themeKey))
            {
                yield return nested;
            }
        }
    }
}
