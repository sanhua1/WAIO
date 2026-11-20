using System.Runtime.InteropServices;

namespace Waio.UI;

/// <summary>
/// 通过 UI Automation 取文字光标位置:焦点元素的 TextPattern2.GetCaretRange(没有就用 TextPattern.GetSelection),
/// 取这段文字范围的屏幕矩形;光标处范围是空的就扩展到一个字符再取。
/// <para>
/// 覆盖 MSAA 和系统插入符都拿不到的程序:自己绘制文字的终端(Windows Terminal 及基于它的终端)、WinUI/UWP 程序、
/// 部分 Electron 程序。跨进程调用可能较慢,<b>调用方应放在后台线程并设超时</b>(见 <see cref="ScreenLocator.CaretRect"/>)。
/// </para>
/// <para>
/// 只用到少数几个方法,直接按 vtable 槽位调用(槽位已对照 Windows SDK 的 UIAutomationClient.h 核对),
/// 不引入整套 UIAutomationClient 互操作程序集。
/// </para>
/// </summary>
internal static partial class UiaCaret
{
    private const int UiaTextPatternId = 10014;
    private const int UiaTextPattern2Id = 10024;
    private const int TextUnitCharacter = 0;
    private const uint ClsCtxInprocServer = 1;
    private const uint CoInitMultithreaded = 0;

    // vtable 槽位(IUnknown 占 0~2)
    private const int SlotGetFocusedElement = 8;     // IUIAutomation
    private const int SlotGetCurrentPatternAs = 14;  // IUIAutomationElement
    private const int SlotGetSelection = 5;          // IUIAutomationTextPattern(2)
    private const int SlotGetCaretRange = 10;        // IUIAutomationTextPattern2
    private const int SlotArrayLength = 3;           // IUIAutomationTextRangeArray
    private const int SlotArrayGetElement = 4;
    private const int SlotExpandToEnclosingUnit = 6; // IUIAutomationTextRange
    private const int SlotGetBoundingRectangles = 10;

    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid IidTextPattern = new("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");
    private static readonly Guid IidTextPattern2 = new("506A921A-FCC9-409F-B23B-37EB74106872");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutPtrFn(nint self, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPatternAsFn(nint self, int patternId, ref Guid iid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCaretRangeFn(nint self, out int isActive, out nint range);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutIntFn(nint self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetElementFn(nint self, int index, out nint range);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ExpandFn(nint self, int unit);

    /// <summary>焦点处文字光标的屏幕矩形(物理像素);拿不到返回 null。会做跨进程调用,不要在 UI 线程调用。</summary>
    public static ScreenRect? Locate()
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
            var range = CaretRange(element, owned);
            return range == 0 ? null : RectOf(range);
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
                CoUninitialize(); // S_OK / S_FALSE 都要配对;RPC_E_CHANGED_MODE(负数)不用
            }
        }
    }

    /// <summary>光标处的文字范围:优先 TextPattern2.GetCaretRange,其次当前选区的第一段。</summary>
    private static nint CaretRange(nint element, List<nint> owned)
    {
        var iid2 = IidTextPattern2;
        if (Call<GetPatternAsFn>(element, SlotGetCurrentPatternAs)(element, UiaTextPattern2Id, ref iid2, out var pattern2) == 0
            && pattern2 != 0)
        {
            owned.Add(pattern2);
            if (Call<GetCaretRangeFn>(pattern2, SlotGetCaretRange)(pattern2, out _, out var caret) == 0 && caret != 0)
            {
                owned.Add(caret);
                return caret;
            }
        }

        var iid = IidTextPattern;
        if (Call<GetPatternAsFn>(element, SlotGetCurrentPatternAs)(element, UiaTextPatternId, ref iid, out var pattern) != 0
            || pattern == 0)
        {
            return 0;
        }

        owned.Add(pattern);
        if (Call<OutPtrFn>(pattern, SlotGetSelection)(pattern, out var ranges) != 0 || ranges == 0)
        {
            return 0;
        }

        owned.Add(ranges);
        if (Call<OutIntFn>(ranges, SlotArrayLength)(ranges, out var count) != 0 || count < 1
            || Call<GetElementFn>(ranges, SlotArrayGetElement)(ranges, 0, out var first) != 0 || first == 0)
        {
            return 0;
        }

        owned.Add(first);
        return first;
    }

    /// <summary>范围的第一个矩形。光标处通常是空范围(没有矩形),这时扩展成一个字符再取。</summary>
    private static ScreenRect? RectOf(nint range)
    {
        if (BoundingRect(range) is { } rect)
        {
            return rect;
        }

        return Call<ExpandFn>(range, SlotExpandToEnclosingUnit)(range, TextUnitCharacter) == 0 ? BoundingRect(range) : null;
    }

    private static ScreenRect? BoundingRect(nint range)
    {
        if (Call<OutPtrFn>(range, SlotGetBoundingRectangles)(range, out var array) != 0 || array == 0)
        {
            return null;
        }

        try
        {
            if (SafeArrayGetLBound(array, 1, out var lower) != 0 || SafeArrayGetUBound(array, 1, out var upper) != 0
                || upper - lower + 1 < 4 || SafeArrayAccessData(array, out var data) != 0)
            {
                return null;
            }

            var values = new double[4]; // 每个矩形是 left, top, width, height
            Marshal.Copy(data, values, 0, 4);
            SafeArrayUnaccessData(array);
            var (left, top) = ((int)Math.Round(values[0]), (int)Math.Round(values[1]));
            var (width, height) = ((int)Math.Round(values[2]), (int)Math.Round(values[3]));
            return height <= 0 ? null : new ScreenRect(left, top, left + Math.Max(width, 1), top + height);
        }
        finally
        {
            SafeArrayDestroy(array);
        }
    }

    private static T Call<T>(nint comObject, int slot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * nint.Size));
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint result);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint array, uint dimension, out int bound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint array, uint dimension, out int bound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(nint array, out nint data);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(nint array);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint array);
}
