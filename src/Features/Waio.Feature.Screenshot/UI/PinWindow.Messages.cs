using System.ComponentModel;
using System.Runtime.InteropServices;
using Waio.Feature.Screenshot.Domain;
using static Waio.Feature.Screenshot.UI.PinNative;

namespace Waio.Feature.Screenshot.UI;

/// <summary>贴图窗口的 Win32 部分:创建、把画好的像素交给分层窗口、鼠标键盘、右键菜单(MAIO PinView 的事件处理 + PinMenu)。</summary>
internal sealed partial class PinWindow
{
    private const string ClassName = "WAIO.Screenshot.Pin";
    private const nuint HintTimer = 1;

    private static readonly Dictionary<nint, PinWindow> Live = [];
    private static readonly WndProc Procedure = OnMessage; // 保持引用,不能被回收
    private static bool _registered;

    private nint _surfaceDc;
    private nint _surfaceBitmap;
    private nint _surfaceBits;
    private (int Width, int Height) _surfaceSize;

    private static nint Create(PinWindow pin)
    {
        var instance = GetModuleHandleW(null);
        if (!_registered)
        {
            var wndClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Style = CsDblClks, // 文字上(客户区)双击选中一段;标题栏区域的双击不需要它
                WndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = instance,
                Cursor = LoadCursorW(0, 32512 /* IDC_ARROW */),
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref wndClass) == 0 && Marshal.GetLastWin32Error() != 1410 /* 类已存在 */)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "注册贴图窗口类失败");
            }

            _registered = true;
        }

        var hwnd = CreateWindowExW(WsExLayered | WsExTopmost | WsExToolWindow, ClassName, "WAIO 贴图", WsPopup, 0, 0, 1, 1, 0, 0, instance, 0);
        if (hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建贴图窗口失败");
        }

        Live[hwnd] = pin;
        return hwnd;
    }

    /// <summary>把预乘 Alpha 的 BGRA 像素交给分层窗口,同时设置位置、大小和整体透明度。</summary>
    private void Present(byte[] bgra, int width, int height, PxPoint position)
    {
        var screen = GetDC(0);
        try
        {
            if (_surfaceSize != (width, height) || _surfaceDc == 0)
            {
                ReleaseSurface();
                var header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
                };
                _surfaceDc = CreateCompatibleDC(screen);
                _surfaceBitmap = CreateDIBSection(screen, ref header, 0, out _surfaceBits, 0, 0);
                if (_surfaceBitmap == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"创建贴图位图失败({width}×{height})");
                }

                SelectObject(_surfaceDc, _surfaceBitmap);
                _surfaceSize = (width, height);
            }

            Marshal.Copy(bgra, 0, _surfaceBits, width * height * 4);
            var point = new Point { X = position.X, Y = position.Y };
            var size = new Size { Width = width, Height = height };
            var source = new Point();
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver, SourceConstantAlpha = (byte)Math.Round(State.Opacity * 255), AlphaFormat = AcSrcAlpha,
            };
            UpdateLayeredWindow(_hwnd, screen, ref point, ref size, _surfaceDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            ReleaseDC(0, screen);
        }
    }

    private void ReleaseSurface()
    {
        if (_surfaceDc != 0)
        {
            DeleteDC(_surfaceDc);
            _surfaceDc = 0;
        }

        if (_surfaceBitmap != 0)
        {
            DeleteObject(_surfaceBitmap);
            _surfaceBitmap = 0;
        }

        _surfaceSize = default;
    }

    private static nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Live.TryGetValue(hwnd, out var pin) && pin.Handle(message, wParam, lParam) is { } result)
        {
            return result;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>处理了返回结果;没处理返回 null(交给系统)。</summary>
    private nint? Handle(uint message, nuint wParam, nint lParam)
    {
        if (HandleText(message, lParam) is { } handled)
        {
            return handled;
        }

        switch (message)
        {
            case WmNcHitTest:
                // 贴图内容上按"标题栏"处理:拖动由系统完成;识别出的文字上(和正在拖选时)交给客户区,用来选择文字;阴影外边距不接鼠标
                var (x, y) = PointFromLParam(lParam);
                if (!ContentRect.Contains(new PxPoint(x, y)))
                {
                    return HtTransparent;
                }

                return _selecting || IsOverText(x, y) ? HtClient : HtCaption;
            case WmNcMouseMove:
                TrackHover(nonClient: true);
                return null;
            case WmNcMouseLeave:
                _hovered = false;
                Redraw();
                return 0;
            case WmNcLButtonDown:
                ClearSelection(); // 在文字以外按下:取消选择,然后照常由系统拖动
                return null;
            case WmActivate:
                _active = (wParam & 0xFFFF) != 0;
                Redraw();
                return null;
            case WmNcLButtonDblClk:
                Perform(PinAction.Close); // MAIO:双击关闭
                return 0;
            case WmNcRButtonDown:
                return 0; // 不进系统的标题栏右键流程(否则弹系统菜单)
            case WmNcRButtonUp:
                // 注意:WM_CONTEXTMENU 是 DefWindowProc 处理这条消息时才发的;这里吞掉了它,所以必须自己弹菜单
                ShowMenu();
                return 0;
            case WmContextMenu: // 键盘菜单键 / Shift+F10
                ShowMenu();
                return 0;
            case WmMouseWheel:
                OnWheel(HighWord(wParam));
                return 0;
            case WmKeyDown or WmSysKeyDown:
                return OnKey((int)wParam) ? 0 : null;
            case WmSysChar:
                return 0; // Alt+键 不要"叮"
            case WmTimer when wParam == HintTimer:
                KillTimer(_hwnd, HintTimer);
                _hint = null;
                Redraw();
                return 0;
        }

        return null;
    }

    private void Redraw() => Place(new PxPoint(ContentRect.X, ContentRect.Y));

    /// <summary>修饰键 + 滚轮:缩放(以鼠标为中心)或调透明度,每格 5%(MAIO scrollWheel)。</summary>
    private void OnWheel(int delta)
    {
        var controls = _controls();
        var time = Environment.TickCount64 / 1000.0;
        switch (PinModifiers.TargetFor(IsDown(VkControl), IsDown(VkMenu), IsDown(VkShift), controls.Zoom, controls.Opacity))
        {
            case PinModifiers.Target.Zoom when _zoomWheel.Feed(delta, time) is var steps and not 0:
                ZoomBy(steps);
                break;
            case PinModifiers.Target.Opacity when _opacityWheel.Feed(delta, time) is var steps and not 0:
                SetOpacity(State.OpacityStepped(steps));
                break;
        }
    }

    /// <summary>贴图上的按键(MAIO PinView.keyDown;⌘ 换成 Ctrl)。</summary>
    private bool OnKey(int key)
    {
        var ctrl = IsDown(VkControl);
        var shift = IsDown(VkShift);
        var alt = IsDown(VkMenu);
        if (key is VkOemPlus or VkOemMinus or VkAdd or VkSubtract)
        {
            var controls = _controls();
            var steps = key is VkOemPlus or VkAdd ? 1 : -1;
            switch (PinModifiers.TargetFor(ctrl, alt, shift, controls.Zoom, controls.Opacity))
            {
                case PinModifiers.Target.Zoom:
                    ZoomBy(steps);
                    return true;
                case PinModifiers.Target.Opacity:
                    SetOpacity(State.OpacityStepped(steps));
                    return true;
            }

            return false;
        }

        switch (key)
        {
            case 'C' when ctrl && !shift && !alt:
                Perform(HasSelection ? PinAction.CopyText : PinAction.Copy); // 选中了文字就复制文字(PixPin 同)
                return true;
            case 'A' when ctrl && !shift && !alt && _text is not null:
                SelectAll();
                return true;
            case 'S' when ctrl && !shift && !alt:
                Perform(PinAction.Save);
                return true;
            case VkEscape when HasSelection:
                ClearSelection(); // 先取消选择,再按一次才关闭
                return true;
            case 'W' when ctrl && !shift && !alt:
            case VkEscape:
                Perform(PinAction.Close);
                return true;
            case 'R' when !ctrl && !alt:
                Apply(s => s.Rotated(clockwise: !shift));
                return true;
            case 'E' when !ctrl && !shift && !alt:
                Perform(PinAction.Edit);
                return true;
            case >= '0' and <= '9' when !ctrl && !shift && !alt:
                SetOpacity(State.WithOpacity(PinState.OpacityForDigit(key - '0')!.Value));
                return true;
        }

        return false;
    }

    /// <summary>右键菜单(MAIO PinMenu)。</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        var opacity = CreatePopupMenu();
        try
        {
            if (HasSelection)
            {
                AppendMenuW(menu, MfString, 12, "复制选中的文字\tCtrl+C");
            }

            AppendMenuW(menu, MfString, 1, HasSelection ? "复制图片" : "复制\tCtrl+C");
            if (_text is not null)
            {
                AppendMenuW(menu, MfString, 13, "复制全部文字");
            }

            AppendMenuW(menu, MfString, 2, "保存…\tCtrl+S");
            AppendMenuW(menu, RecordId is null ? MfGrayed : MfString, 3, "编辑标注\tE");
            AppendMenuW(menu, MfString, 10, "识别文字");
            AppendMenuW(menu, MfString, 11, "翻译");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, 4, "顺时针旋转\tR");
            AppendMenuW(menu, MfString, 5, "逆时针旋转\tShift+R");
            AppendMenuW(menu, MfString, 6, "恢复原始大小");
            for (var percent = 100; percent >= 20; percent -= 20)
            {
                var checkedFlag = (int)Math.Round(State.Opacity * 100) == percent ? MfChecked : 0;
                AppendMenuW(opacity, MfString | checkedFlag, (nuint)(100 + percent), $"{percent}%");
            }

            AppendMenuW(menu, MfPopup, (nuint)opacity, "透明度");
            AppendMenuW(menu, State.ShowsShadow ? MfChecked : MfString, 7, "阴影");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, 8, "关闭\tEsc");
            AppendMenuW(menu, MfString, 9, "关闭全部贴图");

            var cursor = Waio.UI.ScreenLocator.CursorPosition();
            SetForegroundWindow(_hwnd); // 菜单要求窗口在前台,否则点别处不会消失
            var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y, _hwnd, 0);
            PostMessageW(_hwnd, WmNull, 0, 0);
            RunMenuCommand(command);
        }
        finally
        {
            DestroyMenu(menu); // 子菜单随父菜单一起销毁
        }
    }

    private void RunMenuCommand(int command)
    {
        switch (command)
        {
            case 1:
                Perform(PinAction.Copy);
                break;
            case 2:
                Perform(PinAction.Save);
                break;
            case 3:
                Perform(PinAction.Edit);
                break;
            case 4 or 5:
                Apply(s => s.Rotated(clockwise: command == 4));
                break;
            case 6:
                Apply(s => s with { Zoom = Math.Min(1, s.ZoomLimit) });
                break;
            case 7:
                Apply(s => s with { ShowsShadow = !s.ShowsShadow });
                break;
            case 8:
                Perform(PinAction.Close);
                break;
            case 12:
                Perform(PinAction.CopyText);
                break;
            case 13:
                Perform(PinAction.CopyAllText);
                break;
            case 10 or 11:
                Perform(command == 10 ? PinAction.Ocr : PinAction.Translate);
                break;
            case 9:
                Perform(PinAction.CloseAll);
                break;
            case > 100:
                SetOpacity(State.WithOpacity((command - 100) / 100.0));
                break;
        }
    }
}
