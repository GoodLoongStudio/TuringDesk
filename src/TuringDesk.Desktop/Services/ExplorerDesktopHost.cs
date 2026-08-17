using System.Runtime.InteropServices;
using System.Text;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Hosts TuringDesk render windows inside Explorer's wallpaper layer while
/// Explorer keeps ownership of desktop icons, taskbar, tray and context menus.
/// Supports both the classic top-level WorkerW layout and newer Windows 11
/// layouts where SHELLDLL_DefView/WorkerW may live under Progman.
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
    private const int SwShowNoActivate = 4;

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwUpdatenow = 0x0100;
    private const uint RdwAllChildren = 0x0080;

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

        var target = FindWallpaperHost();
        if (target is null || target.Parent == IntPtr.Zero || !IsWindow(target.Parent)) return false;

        var style = GetWindowStyle(windowHandle, GwlStyle);
        style &= ~WsPopup;
        style |= WsChild;
        SetWindowStyle(windowHandle, GwlStyle, style);

        // Preserve WPF's own layered-window state. Window.Opacity can cause WPF
        // to manage WS_EX_LAYERED internally; forcibly adding/removing that flag
        // after HWND creation can produce an attached-but-invisible render target.
        var exStyle = GetWindowStyle(windowHandle, GwlExStyle);
        exStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
        exStyle &= ~WsExAppWindow;
        SetWindowStyle(windowHandle, GwlExStyle, exStyle);

        Marshal.SetLastPInvokeError(0);
        _ = SetParent(windowHandle, target.Parent);
        if (Marshal.GetLastPInvokeError() != 0) return false;

        _ = ShowWindow(windowHandle, SwShowNoActivate);
        var positioned = PositionInHost(
            windowHandle,
            target.Parent,
            target.InsertAfter,
            screenX,
            screenY,
            width,
            height,
            frameChanged: true);

        if (positioned)
        {
            _ = RedrawWindow(
                windowHandle,
                IntPtr.Zero,
                IntPtr.Zero,
                RdwInvalidate | RdwUpdatenow | RdwAllChildren);
        }

        return positioned;
    }

    public static bool IsAttached(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return false;
        var parent = GetParent(windowHandle);
        if (parent == IntPtr.Zero || !IsWindow(parent)) return false;

        var parentClass = GetClassName(parent);
        return parentClass is "WorkerW" or "Progman";
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
        var insertAfter = GetClassName(host) == "Progman"
            ? FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null)
            : HwndBottom;
        if (insertAfter == IntPtr.Zero) insertAfter = HwndBottom;
        return PositionInHost(windowHandle, host, insertAfter, screenX, screenY, width, height, frameChanged: false);
    }

    public static string DescribeAttachment(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return "window-unavailable";
        var parent = GetParent(windowHandle);
        if (parent == IntPtr.Zero || !IsWindow(parent)) return "not-attached";

        var parentClass = GetClassName(parent);
        var grandParent = GetParent(parent);
        if (grandParent != IntPtr.Zero && IsWindow(grandParent))
        {
            return $"{parentClass}:0x{parent.ToInt64():X} <- {GetClassName(grandParent)}:0x{grandParent.ToInt64():X}";
        }

        return $"{parentClass}:0x{parent.ToInt64():X}";
    }

    private static bool PositionInHost(
        IntPtr windowHandle,
        IntPtr host,
        IntPtr insertAfter,
        int screenX,
        int screenY,
        int width,
        int height,
        bool frameChanged)
    {
        if (host == IntPtr.Zero || !IsWindow(host)) return false;

        // Child HWND coordinates are relative to the parent's client area. Use
        // MapWindowPoints instead of subtracting top-level window rectangles;
        // this handles negative coordinates and mixed multi-monitor layouts.
        var origin = new Point { X = screenX, Y = screenY };
        _ = MapWindowPoints(IntPtr.Zero, host, ref origin, 1);

        var flags = SwpNoActivate | SwpShowWindow | (frameChanged ? SwpFrameChanged : 0);
        var placed = SetWindowPos(
            windowHandle,
            insertAfter == IntPtr.Zero ? HwndBottom : insertAfter,
            origin.X,
            origin.Y,
            Math.Max(1, width),
            Math.Max(1, height),
            flags);

        if (placed) _ = ShowWindow(windowHandle, SwShowNoActivate);
        return placed;
    }

    private static DesktopHostTarget? FindWallpaperHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return null;

        RaiseDesktop(progman);

        // IMPORTANT: always prefer a dedicated WorkerW wallpaper host. On newer
        // Windows 11 builds SHELLDLL_DefView can be a direct Progman child while
        // Explorer still creates a separate WorkerW for wallpaper content. The
        // previous implementation returned Progman immediately in that layout;
        // SetParent then succeeded but WPF could remain hidden behind Explorer's
        // actual wallpaper surface. Search every WorkerW path first.
        var wallpaperWorker = FindTopLevelWallpaperWorker();
        if (wallpaperWorker != IntPtr.Zero)
        {
            return new DesktopHostTarget(wallpaperWorker, HwndBottom);
        }

        wallpaperWorker = FindProgmanChildWallpaperWorker(progman);
        if (wallpaperWorker != IntPtr.Zero)
        {
            return new DesktopHostTarget(wallpaperWorker, HwndBottom);
        }

        // Last-resort compatibility path. Keep the render child immediately
        // below Explorer's icon view, but do not mutate WPF's layered-window bit.
        var directDefView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        return directDefView != IntPtr.Zero
            ? new DesktopHostTarget(progman, directDefView)
            : null;
    }

    private static IntPtr FindTopLevelWallpaperWorker()
    {
        var wallpaperWorker = IntPtr.Zero;
        _ = EnumWindows((topLevel, _) =>
        {
            var shellView = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero) return true;

            // The WorkerW immediately following the window that owns DefView is
            // the classic Explorer wallpaper host. This also handles Win11 builds
            // where DefView is directly under top-level Progman.
            var worker = FindWindowEx(IntPtr.Zero, topLevel, "WorkerW", null);
            if (worker == IntPtr.Zero) return true;

            wallpaperWorker = worker;
            return false;
        }, IntPtr.Zero);
        return wallpaperWorker;
    }

    private static IntPtr FindProgmanChildWallpaperWorker(IntPtr progman)
    {
        var childAfter = IntPtr.Zero;
        while (true)
        {
            var worker = FindWindowEx(progman, childAfter, "WorkerW", null);
            if (worker == IntPtr.Zero) return IntPtr.Zero;

            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return worker;
            }

            childAfter = worker;
        }
    }

    private static void RaiseDesktop(IntPtr progman)
    {
        // Different Windows generations react to different 0x052C forms. Sending
        // all known safe variants gives Explorer a chance to materialize the
        // wallpaper WorkerW without replacing Explorer as the shell.
        _ = SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), IntPtr.Zero, SmtoNormal, 1000, out _);
        _ = SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), SmtoNormal, 1000, out _);
        _ = SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
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

    private sealed record DesktopHostTarget(IntPtr Parent, IntPtr InsertAfter);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
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
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Point point, uint points);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
