using Waio.Kit.Input;
using Waio.Kit.Services;

namespace Waio.Feature.Translate.Infrastructure;

// 参考:MAIO Infrastructure/SelectionReader.swift(其参考 pot-desktop src-tauri/src/window.rs 的 selection_translate)
/// <summary>
/// 读取当前程序里选中的文字。
/// <list type="number">
/// <item>先通过 UI Automation 读取(不影响剪贴板;对应 MAIO 的辅助功能接口);</item>
/// <item>读不到时(Chrome、Electron 等常见)模拟 Ctrl+C,读出后把剪贴板恢复原样。
/// 期间用 <see cref="IClipboardService.SuppressChanges"/>,剪贴板历史不会记录这次复制(对应 MAIO 的 RestoredType 标记)。</item>
/// </list>
/// </summary>
internal sealed class SelectionReader(
    Func<CancellationToken, Task<string?>> accessibilitySelection, IClipboardService clipboard, IInputService input)
{
    /// <summary>
    /// 等待目标程序响应 Ctrl+C 的最长时间。MAIO 是 350 ms;Windows 上发 Ctrl+C 之前还要先松开用户按着的 Alt,
    /// 部分 Electron 程序响应也更慢,放宽到 500 ms。
    /// </summary>
    public static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly KeyChord CopyChord = KeyChord.Parse("ctrl+c");

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (await accessibilitySelection(cancellationToken).ConfigureAwait(false) is { } text && !IsBlank(text))
        {
            return text;
        }

        return await CopySelectionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> CopySelectionAsync(CancellationToken cancellationToken)
    {
        using var suppression = clipboard.SuppressChanges();
        var saved = await clipboard.GetAsync(cancellationToken).ConfigureAwait(false);
        var before = clipboard.ChangeCount;
        input.SendKeys(CopyChord);

        var deadline = DateTime.UtcNow + CopyTimeout;
        while (clipboard.ChangeCount == before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        if (clipboard.ChangeCount == before)
        {
            return null; // 没有选中的文字(或目标程序不响应 Ctrl+C):剪贴板没动过,不用恢复
        }

        var copied = await clipboard.GetAsync(cancellationToken).ConfigureAwait(false);
        await clipboard.SetAsync(saved ?? new ClipboardContent(), CancellationToken.None).ConfigureAwait(false);
        return copied?.Text is { } value && !IsBlank(value) ? value : null;
    }

    public static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text);
}
