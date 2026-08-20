using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private const int WmSettingChange = 0x001A;
    private const int WmDeviceChange = 0x0219;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WtsSessionUnlock = 0x8;
    private const int NotifyForThisSession = 0;

    private readonly List<EnhancementWallpaperWindow> _enhancementWallpapers = [];
    private DesktopSearchBarWindow? _desktopSearchBar;
    private DispatcherTimer? _enhancementDisplayTimer;
    private HwndSource? _enhancementSource;
    private string _enhancementDisplaySignature = string.Empty;
    private uint _taskbarCreatedMessage;
    private bool _wtsRegistered;
    private bool _enhancementRefreshQueued;
    private bool _enhancementForceReattachPending;
    private string _enhancementRefreshReason = "periodic";

    internal void EnableEnhancementMode()
    {
        if (ShellSession.IsShellMode) return;

        ShellSession.IsEnhancementMode = true;
        ShellSession.ExitRequested = false;
        Title = "TuringDesk · Desktop Runtime Host";

        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Opacity = 0;

        Loaded += EnhancementMode_Loaded;
        Closing += EnhancementMode_Closing;
        Closed += EnhancementMode_Closed;
    }

    private void EnhancementMode_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ShellSession.IsEnhancementMode || _enhancementWallpapers.Count > 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ShellSession.IsEnhancementMode || _enhancementWallpapers.Count > 0) return;

            InstallEnhancementSystemHooks();
            ReconcileEnhancementMonitors(force: true, forceExplorerReattach: true, reason: "startup");

            // Keep a low-frequency safety net for display drivers that fail to
            // broadcast a topology message. Normal changes are event-driven.
            _enhancementDisplayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _enhancementDisplayTimer.Tick += (_, _) =>
                ReconcileEnhancementMonitors(force: false, forceExplorerReattach: false, reason: "periodic");
            _enhancementDisplayTimer.Start();

            var attached = _enhancementWallpapers.Count(window => window.IsAttached);
            if (attached > 0)
            {
                SceneEngineTrace.Info(
                    "desktop.engine",
                    $"Desktop Engine attached on {attached}/{_enhancementWallpapers.Count} monitor(s); Explorer remains the Windows shell.");
            }
            else
            {
                ShellNotificationService.Publish(
                    "桌面引擎正在等待 Explorer",
                    "Windows 桌面不受影响；顶部 AI 搜索和语音仍可正常使用。",
                    "warning");
            }

            var onboarding = new OnboardingStateStore();
            if (!onboarding.IsCompleted())
            {
                var setup = new FirstRunSetupWindow(_modelStore);
                setup.ShowDialog();
            }

            _desktopSearchBar = new DesktopSearchBarWindow(this);
            _desktopSearchBar.Show();
            _desktopSearchBar.RefreshPosition();

            ShellNotificationService.Publish(
                "TuringDesk AI Desktop 已就绪",
                "直接使用屏幕上方搜索框，或按 Alt+Space 聚焦。右侧设置按钮进入桌面库与综合设置。",
                "shell");

            Hide();
        }), DispatcherPriority.ApplicationIdle);
    }

    private void InstallEnhancementSystemHooks()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _enhancementSource = HwndSource.FromHwnd(hwnd);
        _enhancementSource?.AddHook(EnhancementWndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        try
        {
            _wtsRegistered = WTSRegisterSessionNotification(hwnd, NotifyForThisSession);
        }
        catch
        {
            _wtsRegistered = false;
        }
    }

    private IntPtr EnhancementWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            ExplorerDesktopHost.InvalidateAll();
            QueueEnhancementRefresh(forceExplorerReattach: true, "TaskbarCreated");
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmDisplayChange:
                QueueEnhancementRefresh(forceExplorerReattach: true, "WM_DISPLAYCHANGE");
                break;
            case WmDpiChanged:
                QueueEnhancementRefresh(forceExplorerReattach: false, "WM_DPICHANGED");
                break;
            case WmDeviceChange:
                QueueEnhancementRefresh(forceExplorerReattach: true, "WM_DEVICECHANGE");
                break;
            case WmSettingChange:
                // Work-area and wallpaper transitions are both delivered through
                // WM_SETTINGCHANGE on different Windows builds.
                QueueEnhancementRefresh(forceExplorerReattach: true, "WM_SETTINGCHANGE");
                break;
            case WmWtsSessionChange when wParam.ToInt32() == WtsSessionUnlock:
                ExplorerDesktopHost.InvalidateAll();
                QueueEnhancementRefresh(forceExplorerReattach: true, "WTS_SESSION_UNLOCK");
                break;
        }

        return IntPtr.Zero;
    }

    private void QueueEnhancementRefresh(bool forceExplorerReattach, string reason)
    {
        _enhancementForceReattachPending |= forceExplorerReattach;
        _enhancementRefreshReason = reason;
        if (_enhancementRefreshQueued) return;
        _enhancementRefreshQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _enhancementRefreshQueued = false;
            var forceReattach = _enhancementForceReattachPending;
            _enhancementForceReattachPending = false;
            var pendingReason = _enhancementRefreshReason;
            ReconcileEnhancementMonitors(force: true, forceExplorerReattach: forceReattach, reason: pendingReason);
        }), DispatcherPriority.Background);
    }

    private void ReconcileEnhancementMonitors(bool force, bool forceExplorerReattach, string reason)
    {
        var monitors = DisplayManager.GetMonitors();
        var signature = DisplayManager.GetSignature();
        if (!force &&
            !forceExplorerReattach &&
            signature == _enhancementDisplaySignature &&
            _enhancementWallpapers.Count > 0)
        {
            _desktopSearchBar?.RefreshPosition();
            return;
        }

        var topologyChanged = signature != _enhancementDisplaySignature;
        _enhancementDisplaySignature = signature;
        SceneEngineTrace.Info(
            "display.topology",
            $"reason={reason} changed={topologyChanged} forceReattach={forceExplorerReattach} monitors={monitors.Count} signature={signature}");

        var byId = monitors.ToDictionary(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase);

        // Remove only displays that actually disappeared. Do not destroy every GPU,
        // WebView and audio surface merely because one monitor's DPI changed.
        foreach (var wallpaper in _enhancementWallpapers.ToArray())
        {
            if (byId.ContainsKey(wallpaper.MonitorId)) continue;
            _enhancementWallpapers.Remove(wallpaper);
            try { wallpaper.Close(); } catch { }
        }

        foreach (var monitor in monitors)
        {
            var existing = _enhancementWallpapers.FirstOrDefault(window =>
                string.Equals(window.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.UpdateMonitor(monitor, forceExplorerReattach);
                continue;
            }

            var wallpaper = new EnhancementWallpaperWindow(monitor);
            _enhancementWallpapers.Add(wallpaper);
            wallpaper.Show();
        }

        _desktopSearchBar?.RefreshPosition();
    }

    private void EnhancementMode_Closing(object? sender, CancelEventArgs e)
    {
        if (!ShellSession.IsEnhancementMode || ShellSession.ExitRequested) return;
        e.Cancel = true;
        Hide();
    }

    private void EnhancementMode_Closed(object? sender, EventArgs e)
    {
        ShellSession.IsEnhancementMode = false;
        _enhancementDisplayTimer?.Stop();
        _enhancementDisplayTimer = null;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (_wtsRegistered && hwnd != IntPtr.Zero)
        {
            try { _ = WTSUnRegisterSessionNotification(hwnd); } catch { }
        }
        _wtsRegistered = false;
        _enhancementSource?.RemoveHook(EnhancementWndProc);
        _enhancementSource = null;
        ExplorerDesktopHost.InvalidateAll();

        if (_desktopSearchBar is not null)
        {
            try { _desktopSearchBar.Close(); } catch { }
            _desktopSearchBar = null;
        }

        foreach (var wallpaper in _enhancementWallpapers.ToArray())
        {
            try { wallpaper.Close(); } catch { }
        }
        _enhancementWallpapers.Clear();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
