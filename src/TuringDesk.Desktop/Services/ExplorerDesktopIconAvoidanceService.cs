using System.Runtime.InteropServices;
using System.Windows;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Best-effort Explorer desktop icon avoidance. It only moves icon positions that
/// intersect TuringDesk's reserved search-bar area and never changes the shell.
/// Native ListView positions and the reserved rectangle are both physical pixels.
/// </summary>
public static class ExplorerDesktopIconAvoidanceService
{
    private const int LvmFirst = 0x1000;
    private const int LvmGetItemCount = LvmFirst + 4;
    private const int LvmGetItemPosition = LvmFirst + 16;
    private const int LvmSetItemPosition32 = LvmFirst + 49;

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private const int BaseIconWidth = 104;
    private const int BaseIconHeight = 104;
    private const int BaseRelocateGap = 24;

    public static void ApplyBestEffort(Rect reservedScreenPixels)
    {
        try
        {
            var listView = FindDesktopListView();
            if (listView == IntPtr.Zero || !GetWindowRect(listView, out var listRect)) return;

            var dpi = Math.Max(96u, GetDpiForWindow(listView));
            var scale = dpi / 96d;
            var iconWidth = Math.Max(1, (int)Math.Ceiling(BaseIconWidth * scale));
            var iconHeight = Math.Max(1, (int)Math.Ceiling(BaseIconHeight * scale));
            var relocateGap = Math.Max(1, (int)Math.Ceiling(BaseRelocateGap * scale));

            _ = GetWindowThreadProcessId(listView, out var processId);
            if (processId == 0) return;

            var process = OpenProcess(
                ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryLimitedInformation,
                false,
                processId);
            if (process == IntPtr.Zero) return;

            try
            {
                var pointBytes = Marshal.SizeOf<NativePoint>();
                var remotePoint = VirtualAllocEx(process, IntPtr.Zero, (nuint)pointBytes, MemCommit | MemReserve, PageReadWrite);
                if (remotePoint == IntPtr.Zero) return;

                try
                {
                    var count = (int)SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero);
                    if (count <= 0 || count > 4096) return;

                    for (var index = 0; index < count; index++)
                    {
                        if (SendMessage(listView, LvmGetItemPosition, (IntPtr)index, remotePoint) == IntPtr.Zero) continue;
                        if (!ReadProcessMemory(process, remotePoint, out NativePoint point, (nuint)pointBytes, out _)) continue;

                        var itemRect = new Rect(
                            listRect.Left + point.X,
                            listRect.Top + point.Y,
                            iconWidth,
                            iconHeight);
                        if (!itemRect.IntersectsWith(reservedScreenPixels)) continue;

                        var targetX = point.X;
                        var targetY = (int)Math.Ceiling(reservedScreenPixels.Bottom + relocateGap - listRect.Top);
                        targetY = Math.Max(0, Math.Min(targetY, Math.Max(0, listRect.Bottom - listRect.Top - iconHeight)));

                        var candidate = new Rect(listRect.Left + targetX, listRect.Top + targetY, iconWidth, iconHeight);
                        if (candidate.IntersectsWith(reservedScreenPixels))
                        {
                            targetX = (int)Math.Ceiling(reservedScreenPixels.Right + relocateGap - listRect.Left);
                            targetX = Math.Max(0, Math.Min(targetX, Math.Max(0, listRect.Right - listRect.Left - iconWidth)));
                            targetY = point.Y;
                        }

                        var packed = PackPoint(targetX, targetY);
                        _ = SendMessage(listView, LvmSetItemPosition32, (IntPtr)index, packed);
                    }
                }
                finally
                {
                    _ = VirtualFreeEx(process, remotePoint, 0, MemRelease);
                }
            }
            finally
            {
                _ = CloseHandle(process);
            }
        }
        catch
        {
            // Explorer is never allowed to become less stable because of avoidance.
        }
    }

    private static IntPtr FindDesktopListView()
    {
        var progman = FindWindow("Progman", null);
        var direct = FindListViewUnder(progman);
        if (direct != IntPtr.Zero) return direct;

        IntPtr found = IntPtr.Zero;
        _ = EnumWindows((window, _) =>
        {
            var candidate = FindListViewUnder(window);
            if (candidate == IntPtr.Zero) return true;
            found = candidate;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindListViewUnder(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return IntPtr.Zero;
        var defView = FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static IntPtr PackPoint(int x, int y)
    {
        var packed = ((long)(ushort)y << 16) | (ushort)x;
        return new IntPtr(packed);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, out NativePoint lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
