using Waio.Feature.Clipboard.Domain;

namespace Waio.Feature.Clipboard;

public enum PopupPosition
{
    /// <summary>文字光标处(拿不到时按 <see cref="CaretFallback"/>)。</summary>
    Caret,
    Mouse,
    Center,
}

/// <summary>弹出位置选"文字光标处"、但当前没有文字光标时(不在输入框里、程序不提供光标位置)用哪个位置。</summary>
public enum CaretFallback
{
    Center,
    Mouse,
}

public enum SearchMode
{
    /// <summary>按 Ctrl+F 或点搜索框才搜索;搜索时 ⏎ 结束搜索,再按 ⏎ 才粘贴(输入法打字时的 ⏎ 不会误粘贴)。</summary>
    Explicit,

    /// <summary>打开面板直接打字就是搜索,⏎ 粘贴。</summary>
    TypeToSearch,
}

public enum PreviewMode
{
    /// <summary>按一下空格打开预览,再按一下关闭。</summary>
    Toggle,

    /// <summary>按住空格预览,松开就关闭。</summary>
    Hold,
}

/// <summary>
/// 剪贴板设置(features/clipboard.json)。字段只增不删。默认值与 MAIO 对齐,
/// 均以 MAIO 为准。
/// </summary>
public sealed class ClipboardSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>超过时删除最久没用过的(固定的不删)。</summary>
    public StorageLimit StorageLimit { get; set; } = StorageLimit.Gb2;

    public PopupPosition PopupPosition { get; set; } = PopupPosition.Caret;

    public CaretFallback CaretFallback { get; set; } = CaretFallback.Center;

    public SearchMode SearchMode { get; set; } = SearchMode.Explicit;

    /// <summary>空格预览的方式(<c>toggle</c> 按一下开再按一下关 / <c>hold</c> 按住预览松开关闭)。</summary>
    public PreviewMode PreviewMode { get; set; } = PreviewMode.Toggle;

    /// <summary>选中后切回原窗口并模拟 Ctrl+V;关闭时只放进剪贴板。</summary>
    public bool PasteDirectly { get; set; } = true;

    /// <summary>粘贴过的条目移到最前。</summary>
    public bool MoveToTopOnPaste { get; set; } = true;

    public bool RecordRichText { get; set; } = true;

    public bool RecordImages { get; set; } = true;

    public bool RecordFiles { get; set; } = true;

    /// <summary>
    /// 遵守程序的"不要记录"声明(密码管理器等)。默认 false,与 MAIO 一致:纯本地使用,用户明确选择连敏感内容也记录。
    /// 想避免记下密码管理器复制的内容就改成 true。
    /// </summary>
    public bool RespectExclusionFormats { get; set; }
}

