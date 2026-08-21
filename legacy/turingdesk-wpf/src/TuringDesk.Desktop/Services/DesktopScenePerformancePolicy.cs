using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TuringDesk.Desktop.Services;

internal sealed record ForegroundAppSnapshot(string ExeName, bool IsFullscreen, bool IsMaximized);

/// <summary>
/// Lightweight Win32/DWM foreground-app detection used by the desktop engine.
/// It is independent from the Agent runtime. Fullscreen detection uses physical
/// monitor bounds plus DWM extended-frame bounds so borderless games/F11 windows
/// are not missed because of invisible resize borders.
/// </summary>
internal static class DesktopScenePerformancePolicy
{
    private const uint MonitorDefaultToNearest = 2;
    private const int GwlStyle = -16;
    private const long WsPopup = unchecked((long)0x80000000);
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private static readonly int CurrentProcessId = Environment.ProcessId;

    public static bool ShouldPauseVisualScene() => GetForegroundApp()?.IsFullscreen == true;

    public static ForegroundAppSnapshot? GetForegroundApp()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return null;
        if (IsCloaked(foreground)) return null;

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0 || processId == CurrentProcessId) return null;

        var className = GetClassName(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;

        string exeName;
        try
        {
            using var process = Process.GetProcessById(processId);
            exeName = process.ProcessName + ".exe";
        }
        catch
        {
            exeName = string.Empty;
        }

        var maximized = IsZoomed(foreground);
        if (!GetWindowRect(foreground, out var windowRect))
            return new(exeName, false, maximized);

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return new(exeName, false, maximized);

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return new(exeName, false, maximized);

        var dpi = Math.Max(96u, GetDpiForWindow(foreground));
        var tolerance = Math.Max(3, (int)Math.Ceiling(4d * dpi / 96d));
        var bounds = monitorInfo.Monitor;

        var extended = TryGetExtendedFrameBounds(foreground, out var dwmRect)
            ? dwmRect
            : windowRect;
        var style = GetWindowStyle(foreground);
        var popup = (style & WsPopup) != 0;

        var coversMonitor = Covers(bounds, extended, tolerance) || Covers(bounds, windowRect, tolerance);
        var fullscreen = coversMonitor && (!maximized || popup);

        return new(exeName, fullscreen, maximized && !fullscreen);
    }

    public static bool IsProcessRunning(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        var normalized = Path.GetFileNameWithoutExtension(exeName.Trim());
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        try
        {
            return Process.GetProcessesByName(normalized).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool Covers(Rect monitor, Rect window, int tolerance)
    {
        if (Math.Abs(window.Left - monitor.Left) <= tolerance &&
            Math.Abs(window.Top - monitor.Top) <= tolerance &&
            Math.Abs(window.Right - monitor.Right) <= tolerance &&
            Math.Abs(window.Bottom - monitor.Bottom) <= tolerance)
            return true;

        var intersectionLeft = Math.Max(window.Left, monitor.Left);
        var intersectionTop = Math.Max(window.Top, monitor.Top);
        var intersectionRight = Math.Min(window.Right, monitor.Right);
        var intersectionBottom = Math.Min(window.Bottom, monitor.Bottom);
        var intersectionWidth = Math.Max(0, intersectionRight - intersectionLeft);
        var intersectionHeight = Math.Max(0, intersectionBottom - intersectionTop);
        var monitorWidth = Math.Max(1, monitor.Right - monitor.Left);
        var monitorHeight = Math.Max(1, monitor.Bottom - monitor.Top);
        var coverage = (intersectionWidth * (double)intersectionHeight) / (monitorWidth * (double)monitorHeight);
        return coverage >= 0.995;
    }

    private static bool TryGetExtendedFrameBounds(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        try
        {
            return DwmGetWindowAttribute(
                hwnd,
                DwmwaExtendedFrameBounds,
                out rect,
                Marshal.SizeOf<Rect>()) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttributeInt(
                       hwnd,
                       DwmwaCloaked,
                       out var cloaked,
                       sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static long GetWindowStyle(IntPtr hwnd) => IntPtr.Size == 8
        ? GetWindowLongPtr(hwnd, GwlStyle).ToInt64()
        : GetWindowLong(hwnd, GwlStyle);

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);
}
