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
    private const uint GwHwndPrev = 3;

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

    // For a dedicated WorkerW we want TuringDesk to be the top child inside the
    // wallpaper host. The WorkerW itself still sits behind Explorer's icon view,
    // so this does not place the wallpaper above desktop icons or normal apps.
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private static readonly object AttachmentGate = new();
    private static readonly Dictionary<IntPtr, string> AttachmentStrategies = new();

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

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;
        RaiseDesktop(progman);

        PrepareWallpaperWindow(windowHandle);

        // Do not treat SetParent success as visual success. Windows 11 can expose
        // multiple WorkerW surfaces; an HWND attached to the wrong one is valid
        // structurally but remains hidden by Explorer's actual wallpaper surface.
        foreach (var target in FindWallpaperHosts(progman))
        {
            if (!TryAttachToTarget(windowHandle, target, screenX, screenY, width, height))
                continue;

            lock (AttachmentGate)
                AttachmentStrategies[windowHandle] = target.Strategy;
            return true;
        }

        lock (AttachmentGate)
            AttachmentStrategies.Remove(windowHandle);
        return false;
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
        var hostClass = GetClassName(host);
        var insertAfter = hostClass == "Progman"
            ? FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null)
            : HwndTop;

        if (hostClass == "Progman" && insertAfter == IntPtr.Zero)
            return false;

        var positioned = PositionInHost(
            windowHandle,
            host,
            insertAfter,
            screenX,
            screenY,
            width,
            height,
            frameChanged: false);

        if (positioned && hostClass == "WorkerW")
        {
            _ = BringWindowToTop(windowHandle);
            positioned = VerifyAttachment(windowHandle, host, requireTopChild: true);
        }

        return positioned;
    }

    public static string DescribeAttachment(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return "window-unavailable";
        var parent = GetParent(windowHandle);
        if (parent == IntPtr.Zero || !IsWindow(parent)) return "not-attached";

        string strategy;
        lock (AttachmentGate)
            strategy = AttachmentStrategies.TryGetValue(windowHandle, out var value) ? value : "unknown";

        var parentClass = GetClassName(parent);
        var previous = GetWindow(windowHandle, GwHwndPrev);
        var zState = previous == IntPtr.Zero ? "top-child" : $"below:0x{previous.ToInt64():X}";
        var visible = IsWindowVisible(windowHandle) ? "visible" : "hidden";
        var grandParent = GetParent(parent);
        if (grandParent != IntPtr.Zero && IsWindow(grandParent))
        {
            return $"{strategy}; {parentClass}:0x{parent.ToInt64():X} <- {GetClassName(grandParent)}:0x{grandParent.ToInt64():X}; {zState}; {visible}";
        }

        return $"{strategy}; {parentClass}:0x{parent.ToInt64():X}; {zState}; {visible}";
    }

    private static void PrepareWallpaperWindow(IntPtr windowHandle)
    {
        var style = GetWindowStyle(windowHandle, GwlStyle);
        style &= ~WsPopup;
        style |= WsChild;
        SetWindowStyle(windowHandle, GwlStyle, style);

        // Preserve WPF's own layered-window state. Do not mutate WS_EX_LAYERED
        // after HWND creation: WPF owns that bit and changing it externally can
        // leave an attached HWND whose render target never becomes visible.
        var exStyle = GetWindowStyle(windowHandle, GwlExStyle);
        exStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
        exStyle &= ~WsExAppWindow;
        SetWindowStyle(windowHandle, GwlExStyle, exStyle);
    }

    private static bool TryAttachToTarget(
        IntPtr windowHandle,
        DesktopHostTarget target,
        int screenX,
        int screenY,
        int width,
        int height)
    {
        if (target.Parent == IntPtr.Zero || !IsWindow(target.Parent)) return false;

        Marshal.SetLastPInvokeError(0);
        _ = SetParent(windowHandle, target.Parent);
        if (Marshal.GetLastPInvokeError() != 0 || GetParent(windowHandle) != target.Parent)
            return false;

        var positioned = PositionInHost(
            windowHandle,
            target.Parent,
            target.InsertAfter,
            screenX,
            screenY,
            width,
            height,
            frameChanged: true);
        if (!positioned) return false;

        _ = ShowWindow(windowHandle, SwShowNoActivate);

        // Dedicated WorkerW hosts may already contain a Windows wallpaper child.
        // HWND_BOTTOM put TuringDesk under that surface and produced the exact
        // false-positive symptom: Attached=true while the old wallpaper remained.
        if (target.RequireTopChild)
        {
            _ = BringWindowToTop(windowHandle);
            _ = SetWindowPos(
                windowHandle,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoActivate | 0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | SwpShowWindow);
        }

        _ = RedrawWindow(
            windowHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            RdwInvalidate | RdwUpdatenow | RdwAllChildren);
        _ = RedrawWindow(
            target.Parent,
            IntPtr.Zero,
            IntPtr.Zero,
            RdwInvalidate | RdwUpdatenow | RdwAllChildren);

        return VerifyAttachment(windowHandle, target.Parent, target.RequireTopChild);
    }

    private static bool VerifyAttachment(IntPtr windowHandle, IntPtr expectedParent, bool requireTopChild)
    {
        if (!IsWindow(windowHandle) || !IsWindow(expectedParent)) return false;
        if (GetParent(windowHandle) != expectedParent) return false;
        if (!IsWindowVisible(windowHandle)) return false;
        if (!GetWindowRect(windowHandle, out var rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

        // A WorkerW wallpaper child must be above any wallpaper surface already
        // hosted in that same WorkerW. Desktop icons are in another Explorer layer.
        if (requireTopChild && GetWindow(windowHandle, GwHwndPrev) != IntPtr.Zero)
            return false;

        return true;
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
            insertAfter,
            origin.X,
            origin.Y,
            Math.Max(1, width),
            Math.Max(1, height),
            flags);

        if (placed) _ = ShowWindow(windowHandle, SwShowNoActivate);
        return placed;
    }

    private static IReadOnlyList<DesktopHostTarget> FindWallpaperHosts(IntPtr progman)
    {
        var targets = new List<DesktopHostTarget>();
        var seen = new HashSet<IntPtr>();

        // Windows 11 raised-desktop layout: WorkerW is frequently a direct child
        // of Progman. Prefer this path because it is closest to the actual modern
        // Explorer wallpaper surface.
        var childAfter = IntPtr.Zero;
        while (true)
        {
            var worker = FindWindowEx(progman, childAfter, "WorkerW", null);
            if (worker == IntPtr.Zero) break;

            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero && seen.Add(worker))
                targets.Add(new DesktopHostTarget(worker, HwndTop, "workerw/progman-child", true));

            childAfter = worker;
        }

        // Classic layout: the WorkerW immediately following the top-level window
        // that owns SHELLDLL_DefView is Explorer's wallpaper host.
        _ = EnumWindows((topLevel, _) =>
        {
            var shellView = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero) return true;

            var worker = FindWindowEx(IntPtr.Zero, topLevel, "WorkerW", null);
            if (worker != IntPtr.Zero && seen.Add(worker))
                targets.Add(new DesktopHostTarget(worker, HwndTop, "workerw/classic-sibling", true));
            return true;
        }, IntPtr.Zero);

        // Last-resort compatibility path. When DefView is directly under Progman,
        // place TuringDesk immediately behind DefView. Never put it above DefView,
        // otherwise it would cover desktop icons.
        var directDefView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (directDefView != IntPtr.Zero && seen.Add(progman))
            targets.Add(new DesktopHostTarget(progman, directDefView, "progman/behind-defview", false));

        return targets;
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

    private sealed record DesktopHostTarget(IntPtr Parent, IntPtr InsertAfter, string Strategy, bool RequireTopChild);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

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
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

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
