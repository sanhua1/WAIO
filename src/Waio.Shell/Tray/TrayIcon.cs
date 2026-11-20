using System.ComponentModel;
using System.Runtime.InteropServices;
using static Waio.Shell.Tray.TrayNative;

namespace Waio.Shell.Tray;

/// <summary>
/// 系统托盘图标:一个隐藏的消息窗口 + Shell_NotifyIcon。左键打开设置窗口,右键弹出原生菜单(打开设置 / 退出)。
/// 必须在 UI 线程创建和释放(消息由 WinUI 的消息循环分发)。
/// 资源管理器重启后(收到 TaskbarCreated)会自动重新添加图标。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string ClassName = "WAIO.TrayWindow";
    private const uint CallbackMessage = WmApp + 1;
    private const uint OpenSettingsId = 1, ExitId = 2, RelaunchElevatedId = 3, CheckUpdateId = 4;

    private readonly WndProc _wndProc; // 必须持有引用,否则委托被 GC 回收后窗口过程会崩溃
    private readonly uint _taskbarCreated;
    private readonly bool _elevated;
    private nint _hwnd;
    private nint _icon;
    private bool _ownsIcon;
    private string _tip;
    private bool _disposed;

    public TrayIcon(string tip, bool elevated)
    {
        _tip = tip;
        _elevated = elevated;
        _wndProc = OnMessage;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadTrayIcon();

        var instance = GetModuleHandleW(null);
        var wndClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            ClassName = ClassName,
        };
        if (RegisterClassExW(ref wndClass) == 0 && Marshal.GetLastWin32Error() != 1410 /* 类已存在 */)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "注册托盘窗口类失败");
        }

        _hwnd = CreateWindowExW(0, ClassName, "WAIO Tray", 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
        if (_hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建托盘消息窗口失败");
        }

        AddIcon();
    }

    public event Action? OpenSettingsRequested;

    public event Action? ExitRequested;

    public event Action? RelaunchElevatedRequested;

    /// <summary>托盘菜单"检查更新"。</summary>
    public event Action? CheckUpdateRequested;

    /// <summary>用户点了 <see cref="ShowBalloon"/> 弹出的通知。</summary>
    public event Action? BalloonClicked;

    /// <summary>弹一条系统通知(Windows 10/11 显示为通知横幅,进通知中心)。必须在 UI 线程调用。</summary>
    public void ShowBalloon(string title, string text)
    {
        var data = NewData();
        data.Flags |= NifInfo;
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = text.Length > 255 ? text[..255] : text;
        data.InfoFlags = NiifUser;
        data.BalloonIcon = _icon;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        var data = NewData();
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = NewData();
        Shell_NotifyIconW(NimDelete, ref data);
        DestroyWindow(_hwnd);
        _hwnd = 0;
        UnregisterClassW(ClassName, GetModuleHandleW(null));
        if (_ownsIcon)
        {
            DestroyIcon(_icon);
        }

        _icon = 0;
    }

    /// <summary>
    /// 从 waio.ico 里按系统小图标尺寸(随 DPI:16/20/24…)取一张;文件缺失时退回系统默认图标,托盘照样能用。
    /// </summary>
    private nint LoadTrayIcon()
    {
        if (Hosting.AppIcon.Exists)
        {
            var icon = LoadImageW(
                0, Hosting.AppIcon.Path, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
            if (icon != 0)
            {
                _ownsIcon = true; // LoadImage 从文件加载的图标要自己销毁
                return icon;
            }
        }

        return LoadIconW(0, IdiApplication);
    }

    private NotifyIconData NewData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Hwnd = _hwnd,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _tip.Length > 127 ? _tip[..127] : _tip,
        Info = "",
        InfoTitle = "",
    };

    private void AddIcon()
    {
        var data = NewData();
        Shell_NotifyIconW(NimAdd, ref data);
    }

    private nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated)
        {
            AddIcon(); // 资源管理器崩溃重启后托盘区被清空
            return 0;
        }

        if (message == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                // 左键直接打开设置;右键弹出菜单(打开设置 / 退出)——用户要求
                case WmLButtonUp:
                    OpenSettingsRequested?.Invoke();
                    break;
                case WmRButtonUp or WmContextMenu:
                    ShowMenu();
                    break;
                case NinBalloonUserClick:
                    BalloonClicked?.Invoke();
                    break;
            }

            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>在鼠标位置弹出菜单。必须在 UI 线程调用。</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MfString, OpenSettingsId, "打开设置");
            if (!_elevated)
            {
                AppendMenuW(menu, MfString, RelaunchElevatedId, "以管理员身份重新启动");
            }

            AppendMenuW(menu, MfString, CheckUpdateId, "检查更新");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, ExitId, "退出 WAIO");

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd); // 否则点菜单外部时菜单不会自动关闭(已知的 Win32 行为)
            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, _hwnd, 0);
            PostMessageW(_hwnd, WmNull, 0, 0);

            switch (chosen)
            {
                case OpenSettingsId:
                    OpenSettingsRequested?.Invoke();
                    break;
                case RelaunchElevatedId:
                    RelaunchElevatedRequested?.Invoke();
                    break;
                case CheckUpdateId:
                    CheckUpdateRequested?.Invoke();
                    break;
                case ExitId:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
