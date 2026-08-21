using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Keeps desktop control surfaces compact and inside the usable Windows work area.
/// WPF window Width/Height values are DIPs, so hard-coded 1180x780 windows can
/// overflow a 125%/150% scaled desktop even when the raw pixel resolution looks
/// large enough. This service sizes against the monitor work area (excluding the
/// taskbar) after the HWND and per-monitor DPI are known.
/// </summary>
internal static class CompactToolWindowService
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;

        var profile = window.GetType().Name switch
        {
            nameof(DesktopLibraryWindow) => new WindowProfile(980, 640, 760, 520),
            nameof(HarnessConsoleWindow) => new WindowProfile(980, 660, 760, 520),
            _ => null
        };

        if (profile is null) return;
        FitToWorkArea(window, profile);

        // If DPI, taskbar position, or monitor topology changes while the window
        // is open, re-clamp when it next loses focus. Avoid clamping continuously
        // during DragMove because that makes manual window movement feel sticky.
        window.Deactivated += (_, _) => ClampToWorkArea(window);
    }

    private static void FitToWorkArea(Window window, WindowProfile profile)
    {
        var work = GetWorkArea(window);
        const double edgeMargin = 24;
        var maxWidth = Math.Max(480, work.Width - edgeMargin * 2);
        var maxHeight = Math.Max(360, work.Height - edgeMargin * 2);

        window.MinWidth = Math.Min(profile.MinWidth, maxWidth);
        window.MinHeight = Math.Min(profile.MinHeight, maxHeight);
        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;

        window.Width = Math.Min(profile.PreferredWidth, maxWidth);
        window.Height = Math.Min(profile.PreferredHeight, maxHeight);
        window.Left = work.Left + (work.Width - window.Width) / 2;
        window.Top = work.Top + (work.Height - window.Height) / 2;
    }

    private static void ClampToWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;

        var work = GetWorkArea(window);
        const double margin = 12;

        if (window.Width > work.Width - margin * 2)
            window.Width = Math.Max(window.MinWidth, work.Width - margin * 2);
        if (window.Height > work.Height - margin * 2)
            window.Height = Math.Max(window.MinHeight, work.Height - margin * 2);

        var maxLeft = work.Right - window.Width - margin;
        var maxTop = work.Bottom - window.Height - margin;
        window.Left = Math.Max(work.Left + margin, Math.Min(window.Left, maxLeft));
        window.Top = Math.Max(work.Top + margin, Math.Min(window.Top, maxTop));
    }

    private static Rect GetWorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        return new Rect(
            info.Work.Left / scaleX,
            info.Work.Top / scaleY,
            (info.Work.Right - info.Work.Left) / scaleX,
            (info.Work.Bottom - info.Work.Top) / scaleY);
    }

    private sealed record WindowProfile(double PreferredWidth, double PreferredHeight, double MinWidth, double MinHeight);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
