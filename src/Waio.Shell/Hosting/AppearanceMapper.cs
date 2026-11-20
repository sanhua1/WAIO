using Waio.Core.Storage;
using Waio.UI.Theming;

namespace Waio.Shell.Hosting;

/// <summary>app.json 的外观设置(Core 的存储格式)和 Waio.UI 的 <see cref="GlassOverrides"/> 互转(ADR-0027)。</summary>
internal static class AppearanceMapper
{
    public static GlassOverrides ToOverrides(AppearanceSettings? s)
    {
        // 旧格式(ADR-0026 的"预设 + 微调")含义不同(色调浓度是系统亚克力的参数),整体按默认值处理
        if (s is null || s.Preset is not null)
        {
            return new GlassOverrides();
        }

        return new GlassOverrides
        {
            BlurRadius = s.BlurRadius,
            Saturation = s.Saturation,
            Tint = s.Tint,
            TintOpacity = s.TintOpacity,
            Surface = s.Surface,
            SurfaceOpacity = s.SurfaceOpacity,
            Accent = s.Accent,
            // 圆角不再可调,固定为标准;旧文件里的 corners 忽略
        };
    }

    public static AppearanceSettings ToSettings(GlassOverrides o) => new()
    {
        BlurRadius = o.BlurRadius,
        Saturation = o.Saturation,
        Tint = o.Tint,
        TintOpacity = o.TintOpacity,
        Surface = o.Surface,
        SurfaceOpacity = o.SurfaceOpacity,
        Accent = o.Accent,
    };
}
