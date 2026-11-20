using Waio.Feature.Translate.Domain;

namespace Waio.Feature.Translate;

/// <summary>
/// 翻译设置(features/translate.json;字段、默认值与 MAIO <c>TranslateSettings</c> 一致)。字段只增不删。
/// API Key 不在这里,存在密钥存储(ADR-0014)。配置是不可信输入:读的时候用 <see cref="Normalized"/> 兜底。
/// </summary>
internal sealed class TranslateSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>翻译源实例列表,顺序即显示顺序。</summary>
    public List<ServiceConfig> Services { get; set; } = ServiceConfig.Defaults();

    public HotkeyBehavior HotkeyBehavior { get; set; } = HotkeyBehavior.Smart;

    /// <summary>首选目标语言。</summary>
    public Language TargetLanguage { get; set; } = Language.SimplifiedChinese;

    /// <summary>原文已经是首选目标语言时,翻译成这个。</summary>
    public Language SecondTargetLanguage { get; set; } = Language.English;

    /// <summary>自动删除换行(PDF 断行)。</summary>
    public bool RemoveLineBreaks { get; set; } = true;

    /// <summary>直接打开输入框时窗口放哪。</summary>
    public WindowPosition WindowPosition { get; set; } = WindowPosition.Center;

    /// <summary>翻译选中文字时窗口放哪。</summary>
    public SelectionPosition SelectionWindowPosition { get; set; } = SelectionPosition.Center;

    /// <summary>失去焦点时关闭。</summary>
    public bool CloseOnBlur { get; set; } = true;

    public double FontSize { get; set; } = 14;

    public bool HistoryEnabled { get; set; } = true;

    public int HistoryLimit { get; set; } = 500;

    /// <summary>记住的窗口位置(左上角,屏幕物理像素),仅 <see cref="WindowPosition.Remember"/> 时使用。</summary>
    public List<int>? RememberedOrigin { get; set; }

    public double WindowWidth { get; set; } = 460;

    public static readonly (double Min, double Max) FontSizeRange = (11, 22);

    public static readonly int[] HistoryLimits = [100, 500, 1000, 5000];

    /// <summary>修正手写配置里的非法值:服务列表为 null、id 重复、字号越界等。</summary>
    public TranslateSettings Normalized()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Services = [.. (Services ?? []).Where(s => s is not null && s.Id.Length > 0 && seen.Add(s.Id))];
        foreach (var service in Services)
        {
            service.Options ??= [];
            service.Name = string.IsNullOrWhiteSpace(service.Name) ? service.Spec.DisplayName : service.Name;
        }

        FontSize = Math.Clamp(FontSize, FontSizeRange.Min, FontSizeRange.Max);
        HistoryLimit = Math.Max(1, HistoryLimit);
        WindowWidth = Math.Clamp(WindowWidth, 360, 900);
        TargetLanguage = TargetLanguage == Language.Auto ? Language.SimplifiedChinese : TargetLanguage;
        SecondTargetLanguage = SecondTargetLanguage == Language.Auto ? Language.English : SecondTargetLanguage;
        return this;
    }
}

/// <summary>快捷键(默认 Win+T)的行为。</summary>
internal enum HotkeyBehavior
{
    /// <summary>有选中文字就翻译选中的,没有就打开输入框。</summary>
    Smart,

    /// <summary>总是打开空的输入框。</summary>
    InputOnly,

    /// <summary>只翻译选中的文字,没有选中时提示。</summary>
    SelectionOnly,
}

internal enum WindowPosition
{
    FollowMouse,
    Remember,
    Center,
}

/// <summary>翻译选中文字时窗口放在哪(输入框模式用 <see cref="WindowPosition"/>)。</summary>
internal enum SelectionPosition
{
    Center,
    Mouse,

    /// <summary>选中文字的下方(拿不到位置时用鼠标处)。</summary>
    NearSelection,
}
