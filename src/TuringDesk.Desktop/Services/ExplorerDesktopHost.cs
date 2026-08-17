using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Hosts TuringDesk render windows inside Explorer's wallpaper layer while
/// Explorer keeps ownership of desktop icons, taskbar, tray and context menus.
/// Multiple child windows can be attached at monitor-specific desktop rectangles.
/// </summary>
internal static class ExplorerDesktopHost
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const long WsChild = 0x40000000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExNoActivate = 0x08000000L;

    private const uint SmtoNormal = 0x0000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private static readonly IntPtr HwndBottom = new(1);

    public static bool TryAttach(IntPtr windowHandle)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return TryAttach(windowHandle, left, top, width, height);
    }

    public static bool TryAttach(IntPtr windowHandle, int screenX, int screenY, int width, int height)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return false;

        var host = FindWallpaperHost();
        if (host == IntPtr.Zero) return false;

        var style = GetWindowStyle(windowHandle, GwlStyle);
        style &= ~WsPopup;
        style |= WsChild;
        SetWindowStyle(windowHandle, GwlStyle, style);

        var exStyle = GetWindowStyle(windowHandle, GwlExStyle);
        exStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
        exStyle &= ~WsExAppWindow;
        SetWindowStyle(windowHandle, GwlExStyle, exStyle);

        Marshal.SetLastPInvokeError(0);
        _ = SetParent(windowHandle, host);
        if (Marshal.GetLastPInvokeError() != 0) return false;

        return PositionInHost(windowHandle, host, screenX, screenY, width, height, frameChanged: true);
    }

    public static bool IsAttached(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return false;
        var parent = GetParent(windowHandle);
        return parent != IntPtr.Zero && IsWindow(parent);
    }

    public static bool ResizeToVirtualDesktop(IntPtr windowHandle)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return ResizeToDesktopRect(windowHandle, left, top, width, height);
    }

    public static bool ResizeToDesktopRect(IntPtr windowHandle, int screenX, int screenY, int width, int height)
    {
        if (!IsAttached(windowHandle)) return false;
        var host = GetParent(windowHandle);
        return PositionInHost(windowHandle, host, screenX, screenY, width, height, frameChanged: false);
    }

    private static bool PositionInHost(IntPtr windowHandle, IntPtr host, int screenX, int screenY, int width, int height, bool frameChanged)
    {
        var hostLeft = 0;
        var hostTop = 0;
        if (GetWindowRect(host, out var hostRect))
        {
            hostLeft = hostRect.Left;
            hostTop = hostRect.Top;
        }
        else
        {
            hostLeft = GetSystemMetrics(SmXVirtualScreen);
            hostTop = GetSystemMetrics(SmYVirtualScreen);
        }

        var flags = SwpNoActivate | SwpShowWindow | (frameChanged ? SwpFrameChanged : 0);
        return SetWindowPos(
            windowHandle,
            HwndBottom,
            screenX - hostLeft,
            screenY - hostTop,
            Math.Max(1, width),
            Math.Max(1, height),
            flags);
    }

    private static IntPtr FindWallpaperHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        _ = SendMessageTimeout(
            progman,
            0x052C,
            IntPtr.Zero,
            IntPtr.Zero,
            SmtoNormal,
            1000,
            out _);

        var wallpaperHost = IntPtr.Zero;
        _ = EnumWindows((topLevel, _) =>
        {
            var shellView = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero) return true;

            var worker = FindWindowEx(IntPtr.Zero, topLevel, "WorkerW", null);
            if (worker == IntPtr.Zero) return true;

            wallpaperHost = worker;
            return false;
        }, IntPtr.Zero);

        if (wallpaperHost != IntPtr.Zero) return wallpaperHost;

        return FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero
            ? progman
            : IntPtr.Zero;
    }

    private static long GetWindowStyle(IntPtr hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr(hwnd, index).ToInt64()
        : GetWindowLong(hwnd, index);

    private static void SetWindowStyle(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr(hwnd, index, new IntPtr(value));
        }
        else
        {
            _ = SetWindowLong(hwnd, index, unchecked((int)value));
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
