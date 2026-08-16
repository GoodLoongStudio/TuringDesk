using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TuringDesk.Desktop.Services;

public sealed record DisplayMonitor(
    string Id,
    int Left,
    int Top,
    int Width,
    int Height,
    int WorkLeft,
    int WorkTop,
    int WorkWidth,
    int WorkHeight,
    bool IsPrimary)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public int WorkRight => WorkLeft + WorkWidth;
    public int WorkBottom => WorkTop + WorkHeight;

    public bool ContainsWindowCenter(WindowSnapshot window)
    {
        var x = window.X + Math.Max(0, window.Width / 2);
        var y = window.Y + Math.Max(0, window.Height / 2);
        return x >= Left && x < Right && y >= Top && y < Bottom;
    }
}

public static class DisplayManager
{
    private const uint MonitorInfofPrimary = 0x00000001;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;

            monitors.Add(new DisplayMonitor(
                monitor.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top,
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top,
                (info.dwFlags & MonitorInfofPrimary) != 0));
            return true;
        }, IntPtr.Zero);

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Left)
            .ThenBy(monitor => monitor.Top)
            .ToArray();
    }

    public static DisplayMonitor GetPrimary() =>
        GetMonitors().FirstOrDefault(monitor => monitor.IsPrimary)
        ?? new DisplayMonitor("fallback", 0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight, 0, 0, (int)SystemParameters.WorkArea.Width, (int)SystemParameters.WorkArea.Height, true);

    public static string GetSignature() => string.Join("|", GetMonitors().Select(monitor =>
        $"{monitor.Id}:{monitor.Left},{monitor.Top},{monitor.Width},{monitor.Height}:{monitor.IsPrimary}"));

    public static void PositionWindow(Window window, DisplayMonitor monitor, bool useWorkArea = false, bool topmost = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var x = useWorkArea ? monitor.WorkLeft : monitor.Left;
        var y = useWorkArea ? monitor.WorkTop : monitor.Top;
        var width = useWorkArea ? monitor.WorkWidth : monitor.Width;
        var height = useWorkArea ? monitor.WorkHeight : monitor.Height;

        _ = SetWindowPos(handle, topmost ? HwndTopmost : IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height), SwpNoActivate | SwpShowWindow);
    }

    public static void PositionPopupBottomCenter(Window window, DisplayMonitor monitor, int marginPixels = 10)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var dpi = Math.Max(96u, GetDpiForWindow(handle));
        var scale = dpi / 96d;
        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        var x = monitor.WorkLeft + Math.Max(marginPixels, (monitor.WorkWidth - width) / 2);
        var y = Math.Max(monitor.WorkTop + marginPixels, monitor.WorkBottom - height - marginPixels);

        _ = SetWindowPos(handle, HwndTopmost, x, y, Math.Max(1, width), Math.Max(1, height), SwpNoActivate | SwpShowWindow);
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint cbSize; public NativeRect rcMonitor; public NativeRect rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
}
