using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TuringDesk.Desktop.Services;

internal sealed record ForegroundAppSnapshot(string ExeName, bool IsFullscreen, bool IsMaximized);

/// <summary>
/// Lightweight Win32 foreground-app detection used by the desktop engine. It is
/// intentionally independent from the Agent runtime so wallpaper playback rules
/// keep working even when Harness or a model is offline.
/// </summary>
internal static class DesktopScenePerformancePolicy
{
    private const uint MonitorDefaultToNearest = 2;
    private static readonly int CurrentProcessId = Environment.ProcessId;

    public static bool ShouldPauseVisualScene() => GetForegroundApp()?.IsFullscreen == true;

    public static ForegroundAppSnapshot? GetForegroundApp()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return null;

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

        if (!GetWindowRect(foreground, out var windowRect)) return new(exeName, false, IsZoomed(foreground));
        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return new(exeName, false, IsZoomed(foreground));

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return new(exeName, false, IsZoomed(foreground));

        const int tolerance = 3;
        var bounds = monitorInfo.Monitor;
        var fullscreen = Math.Abs(windowRect.Left - bounds.Left) <= tolerance &&
                         Math.Abs(windowRect.Top - bounds.Top) <= tolerance &&
                         Math.Abs(windowRect.Right - bounds.Right) <= tolerance &&
                         Math.Abs(windowRect.Bottom - bounds.Bottom) <= tolerance;
        return new(exeName, fullscreen, IsZoomed(foreground));
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);
}
