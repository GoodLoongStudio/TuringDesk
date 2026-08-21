using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services.SceneEngine;

internal sealed record DesktopInputSnapshot(double X, double Y, double NormalizedX, double NormalizedY, bool LeftDown);

internal static class DesktopInputBridge
{
    private const int VkLButton = 0x01;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static DesktopInputSnapshot? Capture()
    {
        if (!GetCursorPos(out var point)) return null;
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        var x = point.X - left;
        var y = point.Y - top;
        var nx = Math.Clamp(x / (double)width, 0, 1);
        var ny = Math.Clamp(y / (double)height, 0, 1);
        var leftDown = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
        return new DesktopInputSnapshot(x, y, nx, ny, leftDown);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
