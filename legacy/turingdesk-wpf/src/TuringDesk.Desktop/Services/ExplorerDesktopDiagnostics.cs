using System.Runtime.InteropServices;
using System.Text;

namespace TuringDesk.Desktop.Services;

internal static class ExplorerDesktopDiagnostics
{
    private const uint GwChild = 5;
    private const uint GwHwndNext = 2;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    public static void Capture(string reason)
    {
        try
        {
            SceneEngineTrace.Info("explorer.topology", $"capture-start reason={reason}");

            var progman = FindWindow("Progman", null);
            LogWindow("progman", progman);
            if (progman != IntPtr.Zero)
            {
                LogChildren("progman-child", progman, 40);
            }

            var topWorkers = new List<IntPtr>();
            _ = EnumWindows((hwnd, _) =>
            {
                if (string.Equals(GetClassName(hwnd), "WorkerW", StringComparison.Ordinal))
                    topWorkers.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            SceneEngineTrace.Info("explorer.topology", $"top-level-workerw-count={topWorkers.Count}");
            for (var index = 0; index < topWorkers.Count; index++)
            {
                var worker = topWorkers[index];
                LogWindow($"top-workerw[{index}]", worker);
                LogChildren($"top-workerw[{index}]-child", worker, 24);
            }

            SceneEngineTrace.Info("explorer.topology", "capture-end");
        }
        catch (Exception error)
        {
            SceneEngineTrace.Error("explorer.topology", $"capture failed reason={reason}", error);
        }
    }

    private static void LogChildren(string label, IntPtr parent, int max)
    {
        var child = GetWindow(parent, GwChild);
        var count = 0;
        while (child != IntPtr.Zero && count < max)
        {
            LogWindow($"{label}[{count}]", child);
            child = GetWindow(child, GwHwndNext);
            count++;
        }
        SceneEngineTrace.Info("explorer.topology", $"{label}-count={count}");
    }

    private static void LogWindow(string label, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            SceneEngineTrace.Info("explorer.window", $"{label}=0x0");
            return;
        }

        var className = GetClassName(hwnd);
        var parent = GetParent(hwnd);
        var parentClass = parent == IntPtr.Zero ? "none" : GetClassName(parent);
        var visible = IsWindowVisible(hwnd);
        var style = GetWindowStyle(hwnd, GwlStyle);
        var exStyle = GetWindowStyle(hwnd, GwlExStyle);
        var rectText = GetWindowRect(hwnd, out var rect)
            ? $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) {Math.Max(0, rect.Right - rect.Left)}x{Math.Max(0, rect.Bottom - rect.Top)}"
            : "rect-unavailable";

        var defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
        var workerChild = FindWindowEx(hwnd, IntPtr.Zero, "WorkerW", null);

        SceneEngineTrace.Info(
            "explorer.window",
            $"{label}=0x{hwnd.ToInt64():X} class={className} parent=0x{parent.ToInt64():X}/{parentClass} visible={visible} rect={rectText} style=0x{style:X} exStyle=0x{exStyle:X} directDefView=0x{defView.ToInt64():X} directWorkerW=0x{workerChild.ToInt64():X}");
    }

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var buffer = new StringBuilder(128);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static long GetWindowStyle(IntPtr hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr(hwnd, index).ToInt64()
        : GetWindowLong(hwnd, index);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);
}
