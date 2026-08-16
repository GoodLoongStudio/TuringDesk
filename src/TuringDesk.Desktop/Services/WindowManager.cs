using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TuringDesk.Desktop.Services;

public sealed record WindowSnapshot(
    string Handle,
    string Title,
    int ProcessId,
    string ProcessName,
    int X,
    int Y,
    int Width,
    int Height);

public sealed class WindowManager
{
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint SpiGetWorkArea = 0x0030;

    public async Task<IntPtr> WaitForWindowAsync(IEnumerable<string> titleTokens, TimeSpan timeout)
    {
        var tokens = titleTokens.Select(x => x.ToLowerInvariant()).ToArray();
        var until = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < until)
        {
            var handle = FindFirst(tokens);
            if (handle != IntPtr.Zero) return handle;
            await Task.Delay(250);
        }

        return IntPtr.Zero;
    }

    public IReadOnlyList<WindowSnapshot> ListWindows()
    {
        var windows = new List<WindowSnapshot>();
        EnumWindows((hWnd, _) =>
        {
            var snapshot = Snapshot(hWnd);
            if (snapshot is not null) windows.Add(snapshot);
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public WindowSnapshot? Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var token = query.Trim().ToLowerInvariant();
        return ListWindows().FirstOrDefault(window =>
            window.Title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            window.ProcessName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    public IntPtr FindFirst(IEnumerable<string> titleTokens)
    {
        var tokens = titleTokens.Select(token => token.ToLowerInvariant()).ToArray();
        IntPtr result = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            var snapshot = Snapshot(hWnd);
            if (snapshot is null) return true;

            var title = snapshot.Title.ToLowerInvariant();
            var process = snapshot.ProcessName.ToLowerInvariant();
            if (tokens.Any(token => title.Contains(token) || process.Contains(token)))
            {
                result = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public string? GetForegroundHandle()
    {
        var hWnd = GetForegroundWindow();
        return hWnd == IntPtr.Zero || !IsManageableWindow(hWnd)
            ? null
            : hWnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Focus(string handle)
    {
        if (!TryResolveHandle(handle, out var hWnd) || !IsManageableWindow(hWnd)) return false;
        ShowWindow(hWnd, SwRestore);
        return SetForegroundWindow(hWnd);
    }

    public bool ToggleTask(string handle)
    {
        if (!TryResolveHandle(handle, out var hWnd) || !IsManageableWindow(hWnd)) return false;

        if (GetForegroundWindow() == hWnd)
        {
            _ = ShowWindow(hWnd, SwMinimize);
            return true;
        }

        _ = ShowWindow(hWnd, SwRestore);
        return SetForegroundWindow(hWnd);
    }

    public int MinimizeAll()
    {
        var count = 0;
        EnumWindows((hWnd, ignored) =>
        {
            if (IsManageableWindow(hWnd))
            {
                _ = ShowWindow(hWnd, SwMinimize);
                count++;
            }
            return true;
        }, IntPtr.Zero);
        return count;
    }

    public bool Move(string handle, int x, int y)
    {
        if (!TryResolveHandle(handle, out var hWnd) || !TryGetRect(hWnd, out var rect) || !IsManageableWindow(hWnd))
        {
            return false;
        }

        var area = GetWorkArea();
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var clampedX = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        var clampedY = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));

        ShowWindow(hWnd, SwRestore);
        return MoveWindow(hWnd, clampedX, clampedY, width, height, true);
    }

    public bool Resize(string handle, int width, int height)
    {
        if (!TryResolveHandle(handle, out var hWnd) || !TryGetRect(hWnd, out var rect) || !IsManageableWindow(hWnd))
        {
            return false;
        }

        var area = GetWorkArea();
        var safeWidth = Math.Clamp(width, 320, Math.Max(320, area.Right - area.Left));
        var safeHeight = Math.Clamp(height, 200, Math.Max(200, area.Bottom - area.Top));
        var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - safeWidth));
        var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - safeHeight));

        ShowWindow(hWnd, SwRestore);
        return MoveWindow(hWnd, x, y, safeWidth, safeHeight, true);
    }

    public bool TileSideBySide(string leftHandle, string rightHandle)
    {
        if (!TryResolveHandle(leftHandle, out var left) || !TryResolveHandle(rightHandle, out var right)) return false;
        if (!IsManageableWindow(left) || !IsManageableWindow(right) || left == right) return false;
        TileSideBySide(left, right);
        return true;
    }

    public void TileSideBySide(IntPtr left, IntPtr right)
    {
        var area = GetWorkArea();
        var width = area.Right - area.Left;
        var height = area.Bottom - area.Top;
        var half = (int)Math.Floor(width / 2d);

        ShowWindow(left, SwRestore);
        ShowWindow(right, SwRestore);
        MoveWindow(left, area.Left, area.Top, half, height, true);
        MoveWindow(right, area.Left + half, area.Top, width - half, height, true);
        SetForegroundWindow(right);
    }

    private static WindowSnapshot? Snapshot(IntPtr hWnd)
    {
        if (!IsManageableWindow(hWnd) || !TryGetRect(hWnd, out var rect)) return null;

        _ = GetWindowThreadProcessId(hWnd, out var processIdRaw);
        if (processIdRaw == 0 || processIdRaw > int.MaxValue) return null;
        var processId = (int)processIdRaw;
        var processName = "unknown";

        try
        {
            processName = Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            // A process can disappear between EnumWindows and inspection.
        }

        return new WindowSnapshot(
            hWnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetTitle(hWnd),
            processId,
            processName,
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    private static bool IsManageableWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !IsWindowVisible(hWnd)) return false;
        if (string.IsNullOrWhiteSpace(GetTitle(hWnd))) return false;

        _ = GetWindowThreadProcessId(hWnd, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }

    private static bool TryResolveHandle(string value, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (!long.TryParse(value, out var raw) || raw == 0) return false;
        handle = new IntPtr(raw);
        return true;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool TryGetRect(IntPtr hWnd, out NativeRect rect) => GetWindowRect(hWnd, out rect);

    private static NativeRect GetWorkArea()
    {
        if (SystemParametersInfo(SpiGetWorkArea, 0, out var area, 0)) return area;
        return new NativeRect
        {
            Left = 0,
            Top = 0,
            Right = GetSystemMetrics(0),
            Bottom = GetSystemMetrics(1)
        };
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out NativeRect pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
