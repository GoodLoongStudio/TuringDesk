using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TuringDesk.Desktop.Services;

public sealed class WindowManager
{
    private const int SwRestore = 9;

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

    public IntPtr FindFirst(IEnumerable<string> titleTokens)
    {
        var tokens = titleTokens.ToArray();
        IntPtr result = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var title = GetTitle(hWnd).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(title)) return true;

            if (tokens.Any(title.Contains))
            {
                result = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public void TileSideBySide(IntPtr left, IntPtr right)
    {
        var area = SystemParameters.WorkArea;
        var half = (int)Math.Floor(area.Width / 2);
        var top = (int)area.Top;
        var height = (int)area.Height;

        ShowWindow(left, SwRestore);
        ShowWindow(right, SwRestore);
        MoveWindow(left, (int)area.Left, top, half, height, true);
        MoveWindow(right, (int)area.Left + half, top, (int)area.Width - half, height, true);
        SetForegroundWindow(right);
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
