using System.Globalization;
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
    bool IsPrimary,
    string DeviceName = "",
    uint DpiX = 96,
    uint DpiY = 96)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public int WorkRight => WorkLeft + WorkWidth;
    public int WorkBottom => WorkTop + WorkHeight;
    public double ScaleX => Math.Max(96u, DpiX) / 96d;
    public double ScaleY => Math.Max(96u, DpiY) / 96d;

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
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDefaultDpi = 96;
    private const int MonitorDpiTypeEffective = 0;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                szDevice = string.Empty
            };
            if (!GetMonitorInfo(monitor, ref info)) return true;

            var deviceName = info.szDevice?.Trim() ?? string.Empty;
            var id = !string.IsNullOrWhiteSpace(deviceName)
                ? deviceName
                : $"hmonitor:{monitor.ToInt64().ToString(CultureInfo.InvariantCulture)}";
            var (dpiX, dpiY) = GetMonitorDpi(monitor);

            monitors.Add(new DisplayMonitor(
                id,
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top,
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top,
                (info.dwFlags & MonitorInfofPrimary) != 0,
                deviceName,
                dpiX,
                dpiY));
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
        ?? new DisplayMonitor(
            "fallback",
            0,
            0,
            Math.Max(1, (int)SystemParameters.PrimaryScreenWidth),
            Math.Max(1, (int)SystemParameters.PrimaryScreenHeight),
            0,
            0,
            Math.Max(1, (int)SystemParameters.WorkArea.Width),
            Math.Max(1, (int)SystemParameters.WorkArea.Height),
            true,
            "fallback",
            MonitorDefaultDpi,
            MonitorDefaultDpi);

    public static DisplayMonitor? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return GetMonitors().FirstOrDefault(monitor =>
            string.Equals(monitor.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static DisplayMonitor GetForWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            var nativeMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (nativeMonitor != IntPtr.Zero)
            {
                var info = new MonitorInfoEx
                {
                    cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                    szDevice = string.Empty
                };
                if (GetMonitorInfo(nativeMonitor, ref info))
                {
                    var deviceName = info.szDevice?.Trim() ?? string.Empty;
                    var match = GetMonitors().FirstOrDefault(monitor =>
                        !string.IsNullOrWhiteSpace(deviceName) &&
                        string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) return match;
                }
            }
        }

        return GetPrimary();
    }

    /// <summary>
    /// Includes physical bounds, work area, primary role and effective DPI. A DPI
    /// change must invalidate the wallpaper topology even when pixel resolution did
    /// not change (for example 125% -> 100% on one monitor).
    /// </summary>
    public static string GetSignature() => string.Join("|", GetMonitors().Select(monitor =>
        $"{monitor.Id}:{monitor.Left},{monitor.Top},{monitor.Width},{monitor.Height}:" +
        $"{monitor.WorkLeft},{monitor.WorkTop},{monitor.WorkWidth},{monitor.WorkHeight}:" +
        $"dpi={monitor.DpiX}x{monitor.DpiY}:primary={monitor.IsPrimary}"));

    public static void PositionWindow(Window window, DisplayMonitor monitor, bool useWorkArea = false, bool topmost = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var x = useWorkArea ? monitor.WorkLeft : monitor.Left;
        var y = useWorkArea ? monitor.WorkTop : monitor.Top;
        var width = useWorkArea ? monitor.WorkWidth : monitor.Width;
        var height = useWorkArea ? monitor.WorkHeight : monitor.Height;

        _ = SetWindowPos(
            handle,
            topmost ? HwndTopmost : IntPtr.Zero,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            SwpNoActivate | SwpShowWindow);
    }

    public static void PositionPopupTopCenter(Window window, DisplayMonitor monitor, int topOffsetPixels = 42, int marginPixels = 10)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        // Use the target monitor's DPI, not the window's current DPI. Before the
        // first move (or while crossing monitors) GetDpiForWindow can still report
        // the old monitor and produce a one-frame offset/oversize placement.
        var width = Math.Max(1, (int)Math.Round(window.Width * monitor.ScaleX));
        var height = Math.Max(1, (int)Math.Round(window.Height * monitor.ScaleY));
        var x = monitor.WorkLeft + (monitor.WorkWidth - width) / 2;
        var y = monitor.WorkTop + Math.Max(marginPixels, topOffsetPixels);
        x = Math.Max(monitor.WorkLeft + marginPixels, Math.Min(x, monitor.WorkRight - width - marginPixels));
        y = Math.Max(monitor.WorkTop + marginPixels, Math.Min(y, monitor.WorkBottom - height - marginPixels));
        _ = SetWindowPos(handle, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    public static void PositionPopupBottomCenter(Window window, DisplayMonitor monitor, int marginPixels = 10) =>
        PositionPopup(window, monitor, PopupHorizontal.Center, PopupVertical.Bottom, marginPixels, 0, 0);

    public static void PositionPopupBottomRight(Window window, DisplayMonitor monitor, int marginPixels = 10) =>
        PositionPopup(window, monitor, PopupHorizontal.Right, PopupVertical.Bottom, marginPixels, 0, 0);

    public static void PositionPopupCenter(Window window, DisplayMonitor monitor, int marginPixels = 10) =>
        PositionPopup(window, monitor, PopupHorizontal.Center, PopupVertical.Center, marginPixels, 0, 0);

    public static void PositionAgentCard(Window window, DisplayMonitor monitor, string side, int horizontalOffsetPixels, int bottomOffsetPixels = 0, int marginPixels = 12) =>
        PositionPopup(
            window,
            monitor,
            string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? PopupHorizontal.Left : PopupHorizontal.Right,
            PopupVertical.Bottom,
            marginPixels,
            Math.Max(0, horizontalOffsetPixels),
            Math.Max(0, bottomOffsetPixels));

    private static void PositionPopup(
        Window window,
        DisplayMonitor monitor,
        PopupHorizontal horizontal,
        PopupVertical vertical,
        int marginPixels,
        int horizontalOffsetPixels,
        int bottomOffsetPixels)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var width = Math.Max(1, (int)Math.Round(window.Width * monitor.ScaleX));
        var height = Math.Max(1, (int)Math.Round(window.Height * monitor.ScaleY));

        var x = horizontal switch
        {
            PopupHorizontal.Left => monitor.WorkLeft + marginPixels + horizontalOffsetPixels,
            PopupHorizontal.Right => monitor.WorkRight - width - marginPixels - horizontalOffsetPixels,
            _ => monitor.WorkLeft + (monitor.WorkWidth - width) / 2
        };

        var y = vertical switch
        {
            PopupVertical.Bottom => monitor.WorkBottom - height - marginPixels - bottomOffsetPixels,
            _ => monitor.WorkTop + (monitor.WorkHeight - height) / 2
        };

        x = Math.Max(monitor.WorkLeft + marginPixels, Math.Min(x, monitor.WorkRight - width - marginPixels));
        y = Math.Max(monitor.WorkTop + marginPixels, Math.Min(y, monitor.WorkBottom - height - marginPixels));

        _ = SetWindowPos(handle, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    private static (uint X, uint Y) GetMonitorDpi(IntPtr monitor)
    {
        try
        {
            var result = GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out var dpiY);
            if (result >= 0 && dpiX >= 96 && dpiY >= 96)
                return (dpiX, dpiY);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return (MonitorDefaultDpi, MonitorDefaultDpi);
    }

    private enum PopupHorizontal { Left, Center, Right }
    private enum PopupVertical { Center, Bottom }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
