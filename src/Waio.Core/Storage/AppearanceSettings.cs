namespace Waio.Core.Storage;

/// <summary>
/// app.json 里的外观设置(ADR-0026 / ADR-0027):用户对玻璃的调整,每一项 null = 用默认值。
/// ADR-0027 起没有预设、没有深浅色选项:<see cref="Preset"/>、<see cref="Mode"/>、<see cref="Material"/>、
/// <see cref="LuminosityOpacity"/>、<see cref="KeepWhenInactive"/> 只为兼容旧文件保留,不再使用;读到带 Preset 的旧格式时整体按默认值处理。
/// 只是存储格式;含义和取值范围由 Waio.UI 的 <c>GlassOverrides</c> 解释(Core 不引用 UI,由 Shell 转换)。
/// 字段只增不删(ARCHITECTURE §7)。
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>预设 id:frosted / clear / night / aurora / solid。</summary>
    public string? Preset { get; set; }

    /// <summary>system / light / dark。</summary>
    public string? Mode { get; set; }

    /// <summary>base / thin。</summary>
    public string? Material { get; set; }

    /// <summary>背景色调 "#RRGGBB"。</summary>
    public string? Tint { get; set; }

    public double? TintOpacity { get; set; }

    public double? LuminosityOpacity { get; set; }

    /// <summary>卡片底色 "#RRGGBB"。</summary>
    public string? Surface { get; set; }

    public double? SurfaceOpacity { get; set; }

    /// <summary>强调色 "#RRGGBB",或 "system" 表示跟随系统。</summary>
    public string? Accent { get; set; }

    /// <summary>standard / rounded / extraRounded。</summary>
    public string? Corners { get; set; }

    public bool? KeepWhenInactive { get; set; }

    /// <summary>模糊强度(物理像素,ADR-0027)。</summary>
    public double? BlurRadius { get; set; }

    /// <summary>鲜艳度,1 = 原样(ADR-0027)。</summary>
    public double? Saturation { get; set; }
}
