using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Keeps interactive desktop widgets in the Explorer desktop band: above the
/// desktop/icon host, but below every normal application window. This preserves
/// a real top-level HWND for WPF input without turning the widget into a global
/// always-on-top overlay.
/// </summary>
internal static class DesktopWidgetZOrderService
{
    private const uint GwHwndPrev = 3;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly ConcurrentDictionary<IntPtr, byte> PositionedWindows = new();

    public static bool PositionAboveExplorerDesktop(
        IntPtr windowHandle,
        int screenX,
        int screenY,
        int width,
        int height)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return false;

        // The Explorer desktop band only needs to be established once for a live
        // HWND. Search-result expansion can resize the window many times per second;
        // those hot-path resizes must never enumerate Explorer or rewrite global
        // top-level Z order because that can force a DWM/Explorer recomposition.
        if (PositionedWindows.ContainsKey(windowHandle))
        {
            return MoveResizeWithoutZOrder(windowHandle, screenX, screenY, width, height);
        }

        var desktopHost = FindDesktopIconHost();
        if (desktopHost == IntPtr.Zero)
            return MoveResizeWithoutZOrder(windowHandle, screenX, screenY, width, height);

        var immediatelyAboveDesktop = GetWindow(desktopHost, GwHwndPrev);
        var inserted = immediatelyAboveDesktop == windowHandle
            ? MoveResizeWithoutZOrder(windowHandle, screenX, screenY, width, height)
            : SetWindowPos(
                windowHandle,
                immediatelyAboveDesktop != IntPtr.Zero ? immediatelyAboveDesktop : HwndTop,
                screenX,
                screenY,
                Math.Max(1, width),
                Math.Max(1, height),
                SwpNoActivate | SwpShowWindow);

        if (inserted)
            PositionedWindows[windowHandle] = 0;

        return inserted;
    }

    private static bool MoveResizeWithoutZOrder(
        IntPtr windowHandle,
        int screenX,
        int screenY,
        int width,
        int height)
    {
        if (!IsWindow(windowHandle))
        {
            PositionedWindows.TryRemove(windowHandle, out _);
            return false;
        }

        return SetWindowPos(
            windowHandle,
            HwndTop,
            screenX,
            screenY,
            Math.Max(1, width),
            Math.Max(1, height),
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    private static IntPtr FindDesktopIconHost()
    {
        var result = IntPtr.Zero;
        _ = EnumWindows((topLevel, _) =>
        {
            if (FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return true;
            }

            result = topLevel;
            return false;
        }, IntPtr.Zero);

        if (result != IntPtr.Zero) return result;

        var progman = FindWindow("Progman", null);
        return progman != IntPtr.Zero
               && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero
            ? progman
            : IntPtr.Zero;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
