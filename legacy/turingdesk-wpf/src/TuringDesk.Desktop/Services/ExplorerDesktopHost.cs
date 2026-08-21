using System.Runtime.InteropServices;
using System.Text;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Hosts TuringDesk render windows inside Explorer's wallpaper layer while
/// Explorer keeps ownership of desktop icons, taskbar, tray and context menus.
///
/// An attachment is valid only for one Explorer desktop generation. The generation
/// is identified by Progman + SHELLDLL_DefView + SysListView32 + Explorer PID.
/// Explorer can rebuild any of those HWNDs after wallpaper changes, explorer.exe
/// restart, display reconfiguration or unlock while leaving an old WorkerW alive.
/// Merely checking that GetParent() is still a WorkerW is therefore insufficient.
/// </summary>
internal static class ExplorerDesktopHost
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwHwndPrev = 3;

    private const long WsChild = 0x40000000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExNoActivate = 0x08000000L;

    private const uint SmtoNormal = 0x0000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwShowNoActivate = 4;

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdatenow = 0x0100;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int GeometryTolerancePixels = 3;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly object AttachmentGate = new();
    private static readonly Dictionary<IntPtr, AttachmentRecord> Attachments = new();

    public static bool TryAttach(IntPtr windowHandle)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return TryAttach(windowHandle, left, top, width, height);
    }

    public static bool TryAttach(IntPtr windowHandle, int screenX, int screenY, int width, int height)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return false;

        InvalidateAttachment(windowHandle);
        var generation = CaptureDesktopGeneration();
        if (generation is null)
        {
            SceneEngineTrace.Info("explorer.attach", "desktop generation unavailable");
            return false;
        }

        RaiseDesktop(generation.Progman);
        // 0x052C may create/rebuild WorkerW/DefView. Capture again so every target
        // belongs to the generation that exists after the message sequence.
        generation = CaptureDesktopGeneration();
        if (generation is null) return false;

        PrepareWallpaperWindow(windowHandle);
        var targets = FindWallpaperHosts(generation);
        SceneEngineTrace.Info(
            "explorer.attach",
            $"begin hwnd=0x{windowHandle.ToInt64():X} desktop={DescribeGeneration(generation)} candidates={targets.Count} rect={screenX},{screenY},{width}x{height}");

        foreach (var target in targets)
        {
            if (!TryAttachToTarget(windowHandle, target, screenX, screenY, width, height))
            {
                SceneEngineTrace.Info(
                    "explorer.attach",
                    $"candidate-failed strategy={target.Strategy} parent=0x{target.Parent.ToInt64():X}");
                continue;
            }

            var record = new AttachmentRecord(
                target.Strategy,
                target.Parent,
                generation.Progman,
                generation.DefView,
                generation.ListView,
                generation.ExplorerProcessId,
                target.RequireTopChild);

            lock (AttachmentGate)
                Attachments[windowHandle] = record;

            if (!VerifyAttachmentRecord(windowHandle, record, screenX, screenY, width, height, verifyGeometry: true))
            {
                InvalidateAttachment(windowHandle);
                continue;
            }

            SceneEngineTrace.Info("explorer.attach", $"success {DescribeAttachment(windowHandle)}");
            return true;
        }

        InvalidateAttachment(windowHandle);
        ExplorerDesktopDiagnostics.Capture("attach-exhausted");
        return false;
    }

    /// <summary>
    /// Returns true only if the HWND is attached to the same live Explorer desktop
    /// generation selected by TryAttach. A surviving stale WorkerW is not healthy.
    /// </summary>
    public static bool IsAttached(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return false;
        AttachmentRecord? record;
        lock (AttachmentGate)
            Attachments.TryGetValue(windowHandle, out record);
        if (record is null) return false;

        return VerifyAttachmentRecord(
            windowHandle,
            record,
            0,
            0,
            0,
            0,
            verifyGeometry: false);
    }

    public static void InvalidateAttachment(IntPtr windowHandle)
    {
        lock (AttachmentGate)
            Attachments.Remove(windowHandle);
    }

    public static void InvalidateAll()
    {
        lock (AttachmentGate)
            Attachments.Clear();
    }

    public static bool ResizeToVirtualDesktop(IntPtr windowHandle)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return ResizeToDesktopRect(windowHandle, left, top, width, height);
    }

    public static bool ResizeToDesktopRect(IntPtr windowHandle, int screenX, int screenY, int width, int height)
    {
        AttachmentRecord? record;
        lock (AttachmentGate)
            Attachments.TryGetValue(windowHandle, out record);

        if (record is null || !VerifyAttachmentRecord(
                windowHandle,
                record,
                0,
                0,
                0,
                0,
                verifyGeometry: false))
        {
            InvalidateAttachment(windowHandle);
            return false;
        }

        var insertAfter = record.Strategy.StartsWith("progman/", StringComparison.Ordinal)
            ? record.DefView
            : HwndTop;

        if (insertAfter != IntPtr.Zero && !IsWindow(insertAfter))
        {
            InvalidateAttachment(windowHandle);
            return false;
        }

        var positioned = PositionInHost(
            windowHandle,
            record.Parent,
            insertAfter,
            screenX,
            screenY,
            width,
            height,
            frameChanged: false);

        if (positioned && record.RequireTopChild)
        {
            _ = BringWindowToTop(windowHandle);
            positioned = GetWindow(windowHandle, GwHwndPrev) == IntPtr.Zero;
        }

        if (!positioned || !VerifyAttachmentRecord(
                windowHandle,
                record,
                screenX,
                screenY,
                width,
                height,
                verifyGeometry: true))
        {
            InvalidateAttachment(windowHandle);
            return false;
        }

        return true;
    }

    public static string DescribeAttachment(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) return "window-unavailable";

        AttachmentRecord? record;
        lock (AttachmentGate)
            Attachments.TryGetValue(windowHandle, out record);

        var parent = GetParent(windowHandle);
        if (parent == IntPtr.Zero || !IsWindow(parent)) return "not-attached";

        var previous = GetWindow(windowHandle, GwHwndPrev);
        var zState = previous == IntPtr.Zero ? "top-child" : $"below:0x{previous.ToInt64():X}";
        var visible = IsWindowVisible(windowHandle) ? "visible" : "hidden";
        var strategy = record?.Strategy ?? "unknown";
        var generation = record is null
            ? "generation=untracked"
            : $"progman=0x{record.Progman.ToInt64():X};defview=0x{record.DefView.ToInt64():X};listview=0x{record.ListView.ToInt64():X};pid={record.ExplorerProcessId}";

        return $"{strategy}; {GetClassName(parent)}:0x{parent.ToInt64():X}; {zState}; {visible}; {generation}";
    }

    private static void PrepareWallpaperWindow(IntPtr windowHandle)
    {
        var style = GetWindowStyle(windowHandle, GwlStyle);
        style &= ~WsPopup;
        style |= WsChild;
        SetWindowStyle(windowHandle, GwlStyle, style);

        // The wallpaper renderer must never consume icon clicks. WS_EX_TRANSPARENT
        // also lets Explorer's icon view receive hit testing while this child paints.
        var exStyle = GetWindowStyle(windowHandle, GwlExStyle);
        exStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
        exStyle &= ~WsExAppWindow;
        SetWindowStyle(windowHandle, GwlExStyle, exStyle);
    }

    private static bool TryAttachToTarget(
        IntPtr windowHandle,
        DesktopHostTarget target,
        int screenX,
        int screenY,
        int width,
        int height)
    {
        if (target.Parent == IntPtr.Zero || !IsWindow(target.Parent)) return false;
        if (target.InsertAfter != IntPtr.Zero && !IsWindow(target.InsertAfter)) return false;

        Marshal.SetLastPInvokeError(0);
        _ = SetParent(windowHandle, target.Parent);
        var parentError = Marshal.GetLastPInvokeError();
        if (parentError != 0 || GetParent(windowHandle) != target.Parent)
        {
            SceneEngineTrace.Info(
                "explorer.attach",
                $"SetParent failed strategy={target.Strategy} error={parentError} actual-parent=0x{GetParent(windowHandle).ToInt64():X}");
            return false;
        }

        if (!PositionInHost(
                windowHandle,
                target.Parent,
                target.InsertAfter,
                screenX,
                screenY,
                width,
                height,
                frameChanged: true))
            return false;

        _ = ShowWindow(windowHandle, SwShowNoActivate);

        if (target.RequireTopChild)
        {
            _ = BringWindowToTop(windowHandle);
            _ = SetWindowPos(
                windowHandle,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoActivate | SwpNoSize | SwpNoMove | SwpShowWindow);
        }

        _ = RedrawWindow(
            windowHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            RdwInvalidate | RdwUpdatenow | RdwAllChildren);
        _ = RedrawWindow(
            target.Parent,
            IntPtr.Zero,
            IntPtr.Zero,
            RdwInvalidate | RdwUpdatenow | RdwAllChildren);

        return VerifyBasicAttachment(windowHandle, target.Parent, target.RequireTopChild);
    }

    private static bool VerifyAttachmentRecord(
        IntPtr windowHandle,
        AttachmentRecord record,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight,
        bool verifyGeometry)
    {
        if (!VerifyBasicAttachment(windowHandle, record.Parent, record.RequireTopChild)) return false;

        var current = CaptureDesktopGeneration();
        if (current is null) return false;
        if (current.Progman != record.Progman ||
            current.DefView != record.DefView ||
            current.ListView != record.ListView ||
            current.ExplorerProcessId != record.ExplorerProcessId)
            return false;

        if (!verifyGeometry) return true;
        if (!GetWindowRect(windowHandle, out var rect)) return false;
        var actualWidth = rect.Right - rect.Left;
        var actualHeight = rect.Bottom - rect.Top;
        return Math.Abs(rect.Left - expectedX) <= GeometryTolerancePixels &&
               Math.Abs(rect.Top - expectedY) <= GeometryTolerancePixels &&
               Math.Abs(actualWidth - Math.Max(1, expectedWidth)) <= GeometryTolerancePixels &&
               Math.Abs(actualHeight - Math.Max(1, expectedHeight)) <= GeometryTolerancePixels;
    }

    private static bool VerifyBasicAttachment(IntPtr windowHandle, IntPtr expectedParent, bool requireTopChild)
    {
        if (!IsWindow(windowHandle) || !IsWindow(expectedParent)) return false;
        if (GetParent(windowHandle) != expectedParent) return false;
        if (!IsWindowVisible(windowHandle)) return false;
        if (!GetWindowRect(windowHandle, out var rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
        if (requireTopChild && GetWindow(windowHandle, GwHwndPrev) != IntPtr.Zero) return false;
        return true;
    }

    private static bool PositionInHost(
        IntPtr windowHandle,
        IntPtr host,
        IntPtr insertAfter,
        int screenX,
        int screenY,
        int width,
        int height,
        bool frameChanged)
    {
        if (host == IntPtr.Zero || !IsWindow(host)) return false;

        // The DisplayManager/monitor rectangles are physical screen pixels. Child
        // coordinates are host-client pixels, so convert with MapWindowPoints rather
        // than mixing WPF DIPs or subtracting virtual-screen origins.
        var origin = new Point { X = screenX, Y = screenY };
        _ = MapWindowPoints(IntPtr.Zero, host, ref origin, 1);

        var flags = SwpNoActivate | SwpShowWindow | (frameChanged ? SwpFrameChanged : 0);
        var placed = SetWindowPos(
            windowHandle,
            insertAfter,
            origin.X,
            origin.Y,
            Math.Max(1, width),
            Math.Max(1, height),
            flags);

        if (placed) _ = ShowWindow(windowHandle, SwShowNoActivate);
        return placed;
    }

    private static IReadOnlyList<DesktopHostTarget> FindWallpaperHosts(DesktopGeneration generation)
    {
        var targets = new List<DesktopHostTarget>();
        var seen = new HashSet<IntPtr>();
        var defViewOwner = GetParent(generation.DefView);

        // Canonical Explorer wallpaper topology: after Progman receives 0x052C,
        // Explorer creates an empty top-level WorkerW immediately behind the
        // top-level window that owns SHELLDLL_DefView. Parenting the renderer into
        // this WorkerW places it above the system wallpaper compositor but below
        // the icon view. This must be preferred even on Windows 11 where DefView is
        // hosted directly by Progman; inserting a child into Progman itself can be
        // hidden behind Windows' own wallpaper composition while still looking like
        // a valid HWND attachment.
        if (defViewOwner != IntPtr.Zero)
        {
            var worker = FindWindowEx(IntPtr.Zero, defViewOwner, "WorkerW", null);
            if (worker != IntPtr.Zero &&
                IsWindow(worker) &&
                FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero &&
                seen.Add(worker))
            {
                targets.Add(new DesktopHostTarget(
                    worker,
                    HwndTop,
                    "workerw/desktop-sibling",
                    RequireTopChild: true));
            }
        }

        // Some Windows 11 layouts expose an empty WorkerW as a Progman child rather
        // than a top-level sibling. It is still a real wallpaper host and should be
        // preferred over the direct-Progman fallback.
        var childAfter = IntPtr.Zero;
        while (true)
        {
            var worker = FindWindowEx(generation.Progman, childAfter, "WorkerW", null);
            if (worker == IntPtr.Zero) break;
            childAfter = worker;

            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) continue;
            if (!seen.Add(worker)) continue;
            targets.Add(new DesktopHostTarget(
                worker,
                HwndTop,
                "workerw/progman-child",
                RequireTopChild: true));
        }

        // Last-resort compatibility fallback only. It preserves functionality on
        // unusual Explorer builds where 0x052C does not expose a usable WorkerW, but
        // it is intentionally no longer the preferred Windows 11 path because a
        // renderer child placed directly in Progman can be completely occluded by
        // the system wallpaper layer.
        if (GetParent(generation.DefView) == generation.Progman && seen.Add(generation.Progman))
        {
            targets.Add(new DesktopHostTarget(
                generation.Progman,
                generation.DefView,
                "progman/behind-defview-fallback",
                RequireTopChild: false));
        }

        return targets;
    }

    private static DesktopGeneration? CaptureDesktopGeneration()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero || !IsWindow(progman)) return null;

        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            _ = EnumWindows((topLevel, _) =>
            {
                var candidate = FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (candidate == IntPtr.Zero) return true;
                defView = candidate;
                return false;
            }, IntPtr.Zero);
        }

        if (defView == IntPtr.Zero || !IsWindow(defView)) return null;
        var listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        if (listView == IntPtr.Zero)
            listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero || !IsWindow(listView)) return null;

        _ = GetWindowThreadProcessId(defView, out var processId);
        if (processId == 0) return null;

        return new DesktopGeneration(progman, defView, listView, processId);
    }

    private static string DescribeGeneration(DesktopGeneration generation) =>
        $"progman=0x{generation.Progman.ToInt64():X},defview=0x{generation.DefView.ToInt64():X}," +
        $"listview=0x{generation.ListView.ToInt64():X},pid={generation.ExplorerProcessId}";

    private static void RaiseDesktop(IntPtr progman)
    {
        _ = SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), IntPtr.Zero, SmtoNormal, 1000, out _);
        _ = SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), SmtoNormal, 1000, out _);
        _ = SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static long GetWindowStyle(IntPtr hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr(hwnd, index).ToInt64()
        : GetWindowLong(hwnd, index);

    private static void SetWindowStyle(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
            _ = SetWindowLongPtr(hwnd, index, new IntPtr(value));
        else
            _ = SetWindowLong(hwnd, index, unchecked((int)value));
    }

    private sealed record DesktopGeneration(IntPtr Progman, IntPtr DefView, IntPtr ListView, uint ExplorerProcessId);

    private sealed record DesktopHostTarget(
        IntPtr Parent,
        IntPtr InsertAfter,
        string Strategy,
        bool RequireTopChild);

    private sealed record AttachmentRecord(
        string Strategy,
        IntPtr Parent,
        IntPtr Progman,
        IntPtr DefView,
        IntPtr ListView,
        uint ExplorerProcessId,
        bool RequireTopChild);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Point point, uint points);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}