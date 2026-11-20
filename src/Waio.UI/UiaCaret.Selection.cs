using System.Runtime.InteropServices;

namespace Waio.UI;

/// <summary>
/// 通过 UI Automation 读焦点元素里<b>选中的文字</b>:TextPattern.GetSelection → 每段 GetText,换行连接。
/// 对应 MAIO 的 <c>kAXSelectedTextAttribute</c>(macOS 辅助功能)。不碰剪贴板;读不到时由调用方退回模拟 Ctrl+C。
/// </summary>
internal static partial class UiaCaret
{
    private const int SlotGetText = 12; // IUIAutomationTextRange::GetText(int maxLength, BSTR* text)
    private const int MaxSelectionChars = 100_000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTextFn(nint self, int maxLength, out nint text);

    /// <summary>焦点元素里选中的文字;没有选中、不支持 TextPattern 或出错时返回 null。会跨进程调用,不要在 UI 线程调用。</summary>
    public static string? SelectedText()
    {
        var init = CoInitializeEx(0, CoInitMultithreaded);
        var owned = new List<nint>();
        try
        {
            var clsid = ClsidCUIAutomation;
            var iid = IidIUIAutomation;
            if (CoCreateInstance(ref clsid, 0, ClsCtxInprocServer, ref iid, out var automation) != 0 || automation == 0)
            {
                return null;
            }

            owned.Add(automation);
            if (Call<OutPtrFn>(automation, SlotGetFocusedElement)(automation, out var element) != 0 || element == 0)
            {
                return null;
            }

            owned.Add(element);
            var patternIid = IidTextPattern;
            if (Call<GetPatternAsFn>(element, SlotGetCurrentPatternAs)(element, UiaTextPatternId, ref patternIid, out var pattern) != 0
                || pattern == 0)
            {
                return null;
            }

            owned.Add(pattern);
            if (Call<OutPtrFn>(pattern, SlotGetSelection)(pattern, out var ranges) != 0 || ranges == 0)
            {
                return null;
            }

            owned.Add(ranges);
            if (Call<OutIntFn>(ranges, SlotArrayLength)(ranges, out var count) != 0 || count < 1)
            {
                return null;
            }

            var parts = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                if (Call<GetElementFn>(ranges, SlotArrayGetElement)(ranges, i, out var range) != 0 || range == 0)
                {
                    continue;
                }

                owned.Add(range);
                if (Call<GetTextFn>(range, SlotGetText)(range, MaxSelectionChars, out var bstr) == 0 && bstr != 0)
                {
                    parts.Add(Marshal.PtrToStringBSTR(bstr));
                    Marshal.FreeBSTR(bstr);
                }
            }

            var text = string.Join("\n", parts);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            foreach (var pointer in owned)
            {
                Marshal.Release(pointer);
            }

            if (init >= 0)
            {
                CoUninitialize();
            }
        }
    }
}
