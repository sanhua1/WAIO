using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Waio.Feature.Screenshot.UI;

/// <summary>鼠标指针形状(MAIO updateCursorShape 用到的几种)。</summary>
internal enum CursorShape
{
    Crosshair,
    Arrow,
    /// <summary>可以移动选区(MAIO 的张开的手;Windows 用四向箭头)。</summary>
    Move,
    ResizeNwse,
    ResizeNesw,
    ResizeNs,
    ResizeWe,
    /// <summary>可以选中 / 移动标注。</summary>
    Hand,
    /// <summary>文字工具。</summary>
    IBeam,
}

/// <summary>
/// 遮罩的根元素:能拿键盘焦点(接收 Esc、⏎、方向键),能设置鼠标指针形状(WinUI 的 ProtectedCursor 只有子类能设)。
/// </summary>
internal sealed partial class OverlaySurface : UserControl
{
    private CursorShape? _shape;

    public OverlaySurface()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        SetShape(CursorShape.Crosshair);
    }

    public void SetShape(CursorShape shape)
    {
        if (_shape == shape)
        {
            return;
        }

        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape switch
        {
            CursorShape.Arrow => InputSystemCursorShape.Arrow,
            CursorShape.Move => InputSystemCursorShape.SizeAll,
            CursorShape.ResizeNwse => InputSystemCursorShape.SizeNorthwestSoutheast,
            CursorShape.ResizeNesw => InputSystemCursorShape.SizeNortheastSouthwest,
            CursorShape.ResizeNs => InputSystemCursorShape.SizeNorthSouth,
            CursorShape.ResizeWe => InputSystemCursorShape.SizeWestEast,
            CursorShape.Hand => InputSystemCursorShape.Hand,
            CursorShape.IBeam => InputSystemCursorShape.IBeam,
            _ => InputSystemCursorShape.Cross,
        });
    }
}
