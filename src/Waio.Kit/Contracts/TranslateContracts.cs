using Waio.Kit.Commands;

namespace Waio.Kit.Contracts;

/// <summary>请求翻译一段文字:打开翻译窗口并立即翻译。</summary>
/// <param name="Text">要翻译的文字。</param>
public sealed record TranslateTextInput(string Text);

/// <summary>
/// 翻译功能提供的契约(ADR-0014)。提供方:Translate;调用方:以后的截图 OCR(M5)等。
/// 翻译功能没启用时抛 <c>CommandException(Unavailable)</c>。
/// </summary>
public static class TranslateContracts
{
    public static readonly CommandContract<TranslateTextInput, Unit> TranslateText = new("translate.text");
}
