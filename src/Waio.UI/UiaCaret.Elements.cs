using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Waio.UI;

/// <summary>
/// 截图的"识别控件"(MAIO <c>ElementLocator</c>,用 <c>AXUIElementCopyElementAtPosition</c>):找出某个窗口里鼠标处最深的控件。
/// <para>
/// 不能用 <c>IUIAutomation::ElementFromPoint</c>:截图遮罩盖在最上面,按屏幕位置命中的是遮罩自己(MAIO ADR-0011 同样的问题)。
/// 所以从冻结时记下的目标窗口出发(<c>ElementFromHandle</c>),沿控件视图逐层往下找包含该点的子元素;
/// 同一层有多个子元素包含该点时取面积最小的(最贴近鼠标下的那个控件)。
/// </para>
/// </summary>
internal static partial class UiaCaret
{
    private const int SlotElementFromHandle = 6;       // IUIAutomation
    private const int SlotGetControlViewWalker = 14;   // IUIAutomation::get_ControlViewWalker
    private const int SlotGetFirstChild = 4;           // IUIAutomationTreeWalker::GetFirstChildElement
    private const int SlotGetNextSibling = 6;          // IUIAutomationTreeWalker::GetNextSiblingElement
    private const int SlotGetBoundingRectangle = 43;   // IUIAutomationElement::get_CurrentBoundingRectangle
    private const int MaxDepth = 32;
    private const int MaxSiblings = 400;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FromHandleFn(nint self, nint hwnd, out nint element);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WalkFn(nint self, nint element, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RectFn(nint self, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// <paramref name="window"/> 里包含 <paramref name="point"/> 的最深控件的屏幕矩形(物理像素);找不到返回 null。
    /// 跨进程调用,可能较慢:不要在 UI 线程调用;超过 <paramref name="budget"/> 就返回已经找到的那一层。
    /// </summary>
    public static ScreenRect? ElementRectAt(nint window, ScreenPoint point, TimeSpan budget)
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
            if (Call<FromHandleFn>(automation, SlotElementFromHandle)(automation, window, out var current) != 0 || current == 0
                || Call<OutPtrFn>(automation, SlotGetControlViewWalker)(automation, out var walker) != 0 || walker == 0)
            {
                return null;
            }

            owned.Add(current);
            owned.Add(walker);
            var best = RectOfElement(current);
            var clock = Stopwatch.StartNew();
            for (var depth = 0; depth < MaxDepth && clock.Elapsed < budget; depth++)
            {
                var (child, rect) = SmallestChildContaining(walker, current, point, owned, clock, budget);
                if (child == 0)
                {
                    break;
                }

                current = child;
                best = rect;
            }

            return best;
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

    private static (nint Element, ScreenRect? Rect) SmallestChildContaining(
        nint walker, nint parent, ScreenPoint point, List<nint> owned, Stopwatch clock, TimeSpan budget)
    {
        (nint Element, ScreenRect? Rect, long Area) best = (0, null, long.MaxValue);
        if (Call<WalkFn>(walker, SlotGetFirstChild)(walker, parent, out var child) != 0)
        {
            return (0, null);
        }

        for (var count = 0; child != 0 && count < MaxSiblings && clock.Elapsed < budget; count++)
        {
            owned.Add(child);
            if (RectOfElement(child) is { } rect && Contains(rect, point) && (long)rect.Width * rect.Height < best.Area)
            {
                best = (child, rect, (long)rect.Width * rect.Height);
            }

            if (Call<WalkFn>(walker, SlotGetNextSibling)(walker, child, out var next) != 0)
            {
                break;
            }

            child = next;
        }

        return (best.Element, best.Rect);
    }

    private static ScreenRect? RectOfElement(nint element)
    {
        if (Call<RectFn>(element, SlotGetBoundingRectangle)(element, out var r) != 0 || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            return null;
        }

        return new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static bool Contains(ScreenRect rect, ScreenPoint point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
}
