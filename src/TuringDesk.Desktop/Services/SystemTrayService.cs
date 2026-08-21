using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Lightweight Win32 notification-area integration. This deliberately avoids
/// System.Windows.Forms.NotifyIcon so the WPF desktop does not pull WinForms into
/// the steady-state process just to expose a tray menu.
/// </summary>
internal sealed class SystemTrayService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTray = WmApp + 0x54;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const uint CommandSearch = 1001;
    private const uint CommandSettings = 1002;
    private const uint CommandExit = 1003;

    private readonly Dispatcher _dispatcher;
    private readonly Action _showSearch;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private NotifyIconData _data;
    private bool _added;
    private int _disposed;

    public SystemTrayService(Dispatcher dispatcher, Action showSearch, Action showSettings, Action exit)
    {
        _dispatcher = dispatcher;
        _showSearch = showSearch;
        _showSettings = showSettings;
        _exit = exit;

        var parameters = new HwndSourceParameters("TuringDesk.Tray")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _icon = LoadApplicationIcon();
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray,
            hIcon = _icon,
            szTip = "TuringDesk 图灵桌面",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = Guid.Empty
        };

        _added = Shell_NotifyIcon(NimAdd, ref _data);
        if (_added)
        {
            _data.uTimeoutOrVersion = NotifyIconVersion4;
            _ = Shell_NotifyIcon(NimSetVersion, ref _data);
            DesktopDiagnostics.Info("tray.ready", "native notification-area icon installed");
        }
        else
        {
            DesktopDiagnostics.Info("tray.unavailable", "Shell_NotifyIcon(NIM_ADD) returned false");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTray) return IntPtr.Zero;

        var eventMessage = unchecked((int)(long)lParam);
        switch (eventMessage)
        {
            case WmLButtonDblClk:
                handled = true;
                _dispatcher.BeginInvoke(_showSearch, DispatcherPriority.Normal);
                break;
            case WmRButtonUp:
            case WmContextMenu:
                handled = true;
                ShowContextMenu();
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            _ = AppendMenu(menu, MfString, CommandSearch, "打开图灵搜索");
            _ = AppendMenu(menu, MfString, CommandSettings, "桌面设置");
            _ = AppendMenu(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenu(menu, MfString, CommandExit, "退出 TuringDesk");

            if (!GetCursorPos(out var point)) return;
            _ = SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                _source.Handle,
                IntPtr.Zero);
            _ = PostMessage(_source.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandSearch:
                    _dispatcher.BeginInvoke(_showSearch, DispatcherPriority.Normal);
                    break;
                case CommandSettings:
                    _dispatcher.BeginInvoke(_showSettings, DispatcherPriority.Normal);
                    break;
                case CommandExit:
                    _dispatcher.BeginInvoke(_exit, DispatcherPriority.Send);
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private static IntPtr LoadApplicationIcon()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return IntPtr.Zero;

        try
        {
            _ = ExtractIconEx(path, 0, out var large, out var small, 1);
            if (small != IntPtr.Zero)
            {
                if (large != IntPtr.Zero) _ = DestroyIcon(large);
                return small;
            }
            return large;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_added)
        {
            try { _ = Shell_NotifyIcon(NimDelete, ref _data); } catch { }
            _added = false;
        }

        try { _source.RemoveHook(WndProc); } catch { }
        try { _source.Dispose(); } catch { }
        if (_icon != IntPtr.Zero)
        {
            try { _ = DestroyIcon(_icon); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr largeIcon, out IntPtr smallIcon, uint icons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string itemText);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr reserved);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
