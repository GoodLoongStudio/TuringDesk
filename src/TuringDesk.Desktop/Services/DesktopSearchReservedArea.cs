using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Screen-pixel rectangle reserved by the persistent top-center AI search bar.
/// Desktop surfaces and Explorer best-effort icon avoidance share this source of truth.
/// </summary>
public static class DesktopSearchReservedArea
{
    private static readonly object Gate = new();
    private static Rect? _screenPixels;

    public static event Action<Rect?>? Changed;

    public static Rect? Current
    {
        get { lock (Gate) return _screenPixels; }
    }

    public static void Publish(Window window, int paddingPixels = 18)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;

        var reserved = new Rect(
            rect.Left - paddingPixels,
            rect.Top - paddingPixels,
            Math.Max(1, rect.Right - rect.Left + paddingPixels * 2),
            Math.Max(1, rect.Bottom - rect.Top + paddingPixels * 2));

        lock (Gate) _screenPixels = reserved;
        Changed?.Invoke(reserved);
        ExplorerDesktopIconAvoidanceService.ApplyBestEffort(reserved);
    }

    public static void Clear()
    {
        lock (Gate) _screenPixels = null;
        Changed?.Invoke(null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);
}
