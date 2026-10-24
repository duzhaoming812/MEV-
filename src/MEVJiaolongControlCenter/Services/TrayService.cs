using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MEVJiaolongControlCenter.Core.EC;
using NLog;

namespace MEVJiaolongControlCenter.Services;

/// <summary>
/// 系统托盘（自实现，基于 Shell_NotifyIcon）。
/// 原先用的 Hardcodet.NotifyIcon.Wpf 是 **CPOL 1.02** 许可（非 OSI 认证），
/// 本项目要以 MIT 开源发布，因此这里不依赖任何非宽松许可的托盘库。
/// 提供: 图标 / 提示文本 / 左键唤起窗口 / 右键菜单 / 气泡通知。
/// </summary>
public class TrayService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int WM_TRAYICON = 0x8000 + 1;      // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;

    private const int NIIF_NONE = 0;
    private const int NIIF_INFO = 1;
    private const int NIIF_WARNING = 2;
    private const int NIIF_ERROR = 3;

    private const int TrayIconId = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ExitRequested;
    public event Action<PerformanceMode>? ModeRequested;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _hIcon = IntPtr.Zero;
    private ContextMenu? _menu;
    private MenuItem? _modeMenu;
    private bool _added;
    private bool _ownsIcon;
    private bool _disposed;
    private string _tooltip = "MEV蛟龙控制台";

    public bool IsAvailable => _added;

    public bool Initialize()
    {
        try
        {
            var parameters = new HwndSourceParameters("JccTraySink")
            {
                WindowStyle = 0,   // 不可见的普通窗口: 能收到 TaskbarCreated 广播(消息窗口收不到)
                Width = 0,
                Height = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _hwnd = _source.Handle;

            _hIcon = LoadAppIcon();
            BuildMenu();

            AddIcon();
            Logger.Info(_added
                ? "系统托盘已初始化 (Shell_NotifyIcon)"
                : "系统托盘初始化失败: 图标未注册, 主窗口不会隐藏到托盘");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "系统托盘初始化失败");
            _added = false;
        }

        return _added;
    }

    private void AddIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _hIcon;
        data.szTip = _tooltip;

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_added) Logger.Warn($"添加托盘图标失败, Win32 错误 {Marshal.GetLastWin32Error()}");

        // 自检: 能查到图标矩形就说明真的注册进任务栏了(而不是只有代码执行成功)
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = TrayIconId
        };
        int hr = Shell_NotifyIconGetRect(ref id, out var rect);
        if (hr == 0)
            Logger.Info($"托盘图标已注册: ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
        else
            Logger.Warn($"托盘图标注册后无法查询位置, HRESULT=0x{hr:X8}");
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            switch ((int)lParam)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowWindowRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        // 资源管理器重启后任务栏图标会消失, 必须重新添加
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            Logger.Info("检测到资源管理器重启, 重新注册托盘图标");
            AddIcon();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private uint _taskbarCreatedMessage;

    private void ShowMenu()
    {
        if (_menu == null) return;
        try
        {
            if (!GetCursorPos(out var pt)) return;

            // 隐藏窗口作为 owner 时, ContextMenu 必须用"绝对屏幕坐标"定位;
            // 用 MousePoint 且没有 PlacementTarget 时 WPF 找不到定位基准, 会静默不显示(实测)。
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            _menu.HorizontalOffset = pt.X;
            _menu.VerticalOffset = pt.Y;

            // 不抢到前台的话, 菜单刚弹出就会因为进程没有激活而立刻关闭
            SetForegroundWindow(_hwnd);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            _menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "打开托盘菜单失败");
        }
    }

    private void BuildMenu()
    {
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _menu = new ContextMenu();

        var show = new MenuItem { Header = "显示主界面" };
        show.Click += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(show);
        _menu.Items.Add(new Separator());

        var mode = new MenuItem { Header = "性能模式" };
        AddModeItem(mode, "办公 (安静)", PerformanceMode.Quiet);
        AddModeItem(mode, "游戏 (平衡)", PerformanceMode.Balanced);
        AddModeItem(mode, "狂暴 (满功耗)", PerformanceMode.Turbo);
        _modeMenu = mode;
        _menu.Items.Add(mode);
        _menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(exit);
    }

    /// <summary>EC 不可用时禁用托盘里的性能模式菜单, 避免点了没反应。</summary>
    public void SetModeMenuEnabled(bool enabled)
    {
        if (_modeMenu != null) _modeMenu.IsEnabled = enabled;
    }

    private void AddModeItem(MenuItem parent, string header, PerformanceMode mode)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => ModeRequested?.Invoke(mode);
        parent.Items.Add(item);
    }

    /// <summary>气泡通知。图标未注册时静默返回, 不影响主流程。</summary>
    public void ShowNotification(string title, string message) => ShowBalloon(title, message, NIIF_INFO);

    /// <summary>需要用户注意的提醒(高温等)</summary>
    public void ShowWarning(string title, string message) => ShowBalloon(title, message, NIIF_WARNING);

    private void ShowBalloon(string title, string message, int flags)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;
        try
        {
            var data = NewData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = message;
            data.dwInfoFlags = flags;
            data.uTimeoutOrVersion = 10000;
            if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
                Logger.Debug($"气泡通知失败, Win32 错误 {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "显示气泡通知失败");
        }
    }

    public void UpdateTooltip(string text)
    {
        _tooltip = text ?? "";
        if (!_added || _hwnd == IntPtr.Zero) return;
        try
        {
            var data = NewData();
            data.uFlags = NIF_TIP;
            data.szTip = _tooltip.Length > 120 ? _tooltip[..120] : _tooltip;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "更新托盘提示失败");
        }
    }

    /// <summary>
    /// 直接从 exe 同目录的 app.ico 加载图标(LoadImage), 不引入 System.Drawing.Common。
    /// 返回的 HICON 由本类负责 DestroyIcon。
    /// </summary>
    private IntPtr LoadAppIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(path))
            {
                IntPtr h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (h != IntPtr.Zero)
                {
                    _ownsIcon = true;     // LoadImage 返回的句柄才需要我们 DestroyIcon
                    return h;
                }
                Logger.Warn($"加载 app.ico 失败, Win32 错误 {Marshal.GetLastWin32Error()}, 回退系统图标");
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "加载托盘图标异常, 回退系统图标");
        }
        // LoadIcon(IDI_APPLICATION) 是系统共享图标, 销毁它会破坏其他程序, 不能 DestroyIcon
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_added && _hwnd != IntPtr.Zero)
            {
                var data = NewData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }
        }
        catch { }

        try
        {
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
        }
        catch { }
        _source = null;

        _menu = null;
        if (_ownsIcon && _hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }
}
