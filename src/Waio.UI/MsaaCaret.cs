using System.Runtime.InteropServices;

namespace Waio.UI;

/// <summary>
/// 通过辅助功能接口(MSAA,<c>AccessibleObjectFromWindow(OBJID_CARET)</c> + <c>IAccessible::accLocation</c>)取文字光标位置。
/// Chrome、Edge、Electron、Office 等不用系统插入符的程序大多会通过它报告光标位置,
/// 而 <c>GetGUIThreadInfo</c> 只对用系统插入符的传统控件有效。
/// 思路参考 Ditto(<c>ExternalWindowTracker::FocusCaret</c>,GPL,只借鉴做法):先试 MSAA,再试 GetGUIThreadInfo。
/// </summary>
internal static class MsaaCaret
{
    private const uint ObjIdCaret = 0xFFFFFFF8;
    private const ushort VtI4 = 3;
    private const int ChildIdSelf = 0;

    /// <summary>IAccessible 的 vtable 里 accLocation 的位置:IUnknown 3 个 + IDispatch 4 个 + 前面 15 个 IAccessible 方法。</summary>
    private const int AccLocationSlot = 22;

    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AccLocationFn(nint self, out int left, out int top, out int width, out int height, Variant child);

    /// <summary>光标的屏幕矩形(物理像素);程序不支持或当前没有光标返回 null。</summary>
    public static ScreenRect? Locate(nint window)
    {
        var iid = IidIAccessible;
        nint accessible = 0;
        try
        {
            if (AccessibleObjectFromWindow(window, ObjIdCaret, ref iid, out accessible) != 0 || accessible == 0)
            {
                return null;
            }

            var vtable = Marshal.ReadIntPtr(accessible);
            var fn = Marshal.GetDelegateForFunctionPointer<AccLocationFn>(Marshal.ReadIntPtr(vtable, AccLocationSlot * nint.Size));
            var hr = fn(accessible, out var left, out var top, out var width, out var height, new Variant { Vt = VtI4, Value = ChildIdSelf });

            // 和 Ditto 一样:(0,0) 视为"没有光标"——不少程序在没有光标时返回全零
            if (hr != 0 || (left == 0 && top == 0))
            {
                return null;
            }

            return new ScreenRect(left, top, left + Math.Max(width, 1), top + Math.Max(height, 1));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null; // 目标程序的辅助功能实现有问题:当作拿不到
        }
        finally
        {
            if (accessible != 0)
            {
                Marshal.Release(accessible);
            }
        }
    }

    /// <summary>VARIANT(x64 上 24 字节):这里只用到 VT_I4。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(8)]
        public int Value;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint hwnd, uint objectId, ref Guid riid, out nint accessible);
}
