using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class EnhancementWallpaperWindow : Window
{
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;

    private DisplayMonitor _monitor;
    private readonly ShellSettingsStore _settingsStore = new();
    private readonly SceneCatalogService _sceneCatalog = new();
    private readonly DesktopPlaybackSettingsStore _playbackStore = new();
    private readonly DesktopPlaybackRuleEngine _ruleEngine = new();
    private readonly DispatcherTimer _hostHealthTimer;
    private readonly DispatcherTimer _policyTimer;
    private readonly SemaphoreSlim _sceneLoadGate = new(1, 1);

    private ShellSettings _settings;
    private DesktopPlaybackSettings _playbackSettings;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _attached;
    private bool _maintenanceRunning;
    private bool _policyEvaluationRunning;
    private string? _loadedSceneId;
    private ApplicationRuleAction _lastPolicyAction = ApplicationRuleAction.KeepRunning;
    private string? _lastPolicyTarget;
    private DateTimeOffset _playlistChangedAt = DateTimeOffset.UtcNow;
    private int _playlistIndex;
    private int _sceneLoadVersion;
    private CancellationTokenSource? _sceneLoadCancellation;

    public EnhancementWallpaperWindow(DisplayMonitor monitor)
    {
        _monitor = monitor;
        _settings = _settingsStore.Load();
        _playbackSettings = _playbackStore.Load();
        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Left / monitor.ScaleX;
        Top = monitor.Top / monitor.ScaleY;
        Width = Math.Max(1, monitor.Width / monitor.ScaleX);
        Height = Math.Max(1, monitor.Height / monitor.ScaleY);

        Renderer.PlaybackError += message =>
            ShellNotificationService.Publish("桌面场景播放失败", $"{MonitorLabel}: {message}", "warning");
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        // Explorer/display topology changes are event-driven. This is only a slow
        // recovery safety-net for drivers/shell restarts that fail to broadcast one.
        _hostHealthTimer = new DispatcherTimer(DispatcherPriority.ContextIdle)
        {
            Interval = TimeSpan.FromSeconds(12)
        };
        _hostHealthTimer.Tick += (_, _) => MaintainDesktopHost();

        // Foreground/fullscreen policy must remain responsive, but it uses cached
        // settings and does not touch JSON files. Keep it independent from host health.
        _policyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _policyTimer.Tick += async (_, _) => await EvaluatePlaybackPolicyAsync();
    }

    public bool IsAttached => _attached;
    public string MonitorId => _monitor.Id;
    public string MonitorLabel => _monitor.IsPrimary ? "主显示器" : $"显示器 {_monitor.DeviceName}";

    internal void UpdateMonitor(DisplayMonitor monitor, bool forceReattach = false)
    {
        if (!string.Equals(_monitor.Id, monitor.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot rebind a wallpaper window to a different monitor identity.");

        var geometryChanged = _monitor.Left != monitor.Left ||
                              _monitor.Top != monitor.Top ||
                              _monitor.Width != monitor.Width ||
                              _monitor.Height != monitor.Height ||
                              _monitor.DpiX != monitor.DpiX ||
                              _monitor.DpiY != monitor.DpiY ||
                              _monitor.IsPrimary != monitor.IsPrimary;
        _monitor = monitor;

        if (_windowHandle == IntPtr.Zero) return;
        if (forceReattach)
        {
            ExplorerDesktopHost.InvalidateAttachment(_windowHandle);
            _attached = false;
        }

        if (geometryChanged || forceReattach)
        {
            MaintainExplorerAttachment();
            RequestFreshRender();
            ReportProbe();
        }
    }

    internal void ForceExplorerReattach(string reason)
    {
        if (_windowHandle == IntPtr.Zero) return;
        SceneEngineTrace.Info("explorer.rebind", $"monitor={MonitorId} reason={reason}");
        ExplorerDesktopHost.InvalidateAttachment(_windowHandle);
        _attached = false;
        MaintainExplorerAttachment();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);
        ShellSettingsStore.SettingsChanged += OnShellSettingsChanged;
        DesktopPlaybackSettingsStore.SettingsChanged += OnPlaybackSettingsChanged;

        DisplayManager.PositionWindow(this, _monitor);
        _attached = ExplorerDesktopHost.TryAttach(
            _windowHandle,
            _monitor.Left,
            _monitor.Top,
            _monitor.Width,
            _monitor.Height);

        if (!_attached) Hide();
        else RequestFreshRender();

        ReportProbe();
        _ = RefreshBaseSceneAsync(force: true);
        _policyTimer.Start();
        _hostHealthTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hostHealthTimer.Stop();
        _policyTimer.Stop();
        ShellSettingsStore.SettingsChanged -= OnShellSettingsChanged;
        DesktopPlaybackSettingsStore.SettingsChanged -= OnPlaybackSettingsChanged;
        _sceneLoadVersion++;
        var activeLoad = Interlocked.Exchange(ref _sceneLoadCancellation, null);
        try { activeLoad?.Cancel(); } catch (ObjectDisposedException) { }
        _source?.RemoveHook(WndProc);
        _source = null;
        ExplorerDesktopHost.InvalidateAttachment(_windowHandle);
        Renderer.Stop();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmDpiChanged:
                QueueMonitorRefresh(forceReattach: false, "WM_DPICHANGED");
                break;
            case WmDisplayChange:
                QueueMonitorRefresh(forceReattach: true, "WM_DISPLAYCHANGE");
                break;
            case WmSettingChange:
                QueueMonitorRefresh(forceReattach: true, "WM_SETTINGCHANGE");
                break;
        }

        return IntPtr.Zero;
    }

    private void QueueMonitorRefresh(bool forceReattach, string reason)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var fresh = DisplayManager.Find(_monitor.Id);
            if (fresh is null)
            {
                _attached = false;
                ExplorerDesktopHost.InvalidateAttachment(_windowHandle);
                if (IsVisible) Hide();
                return;
            }

            SceneEngineTrace.Info(
                "display.rebind",
                $"reason={reason} monitor={fresh.Id} rect={fresh.Left},{fresh.Top},{fresh.Width}x{fresh.Height} dpi={fresh.DpiX}x{fresh.DpiY}");
            UpdateMonitor(fresh, forceReattach);
        }), DispatcherPriority.Send);
    }

    private void OnShellSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var latestSettings = _settingsStore.Load();
            var appearanceChanged = AppearanceRequiresReload(_settings.Appearance, latestSettings.Appearance);
            _settings = latestSettings;
            _ = ApplyPlaybackPolicyAsync(forceProfileRefresh: appearanceChanged);
        }), DispatcherPriority.Background);
    }

    private void OnPlaybackSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _playbackSettings = _playbackStore.Load();
            _ = ApplyPlaybackPolicyAsync(forceProfileRefresh: true);
        }), DispatcherPriority.Background);
    }

    private void MaintainDesktopHost()
    {
        if (_maintenanceRunning) return;
        _maintenanceRunning = true;
        try
        {
            var freshMonitor = DisplayManager.Find(_monitor.Id);
            if (freshMonitor is not null)
                UpdateMonitor(freshMonitor);

            MaintainExplorerAttachment();
        }
        catch (Exception error)
        {
            SceneEngineTrace.Error("desktop.health", $"monitor={MonitorId}", error);
        }
        finally
        {
            _maintenanceRunning = false;
        }
    }

    private async Task EvaluatePlaybackPolicyAsync()
    {
        if (_policyEvaluationRunning) return;
        _policyEvaluationRunning = true;
        try
        {
            await ApplyPlaybackPolicyAsync(forceProfileRefresh: false);
        }
        catch (Exception error)
        {
            SceneEngineTrace.Error("desktop.policy", $"monitor={MonitorId}", error);
        }
        finally
        {
            _policyEvaluationRunning = false;
        }
    }

    private static bool AppearanceRequiresReload(ShellAppearanceSettings previous, ShellAppearanceSettings current) =>
        !string.Equals(previous.SceneId, current.SceneId, StringComparison.OrdinalIgnoreCase) ||
        previous.SceneMotionEnabled != current.SceneMotionEnabled ||
        Math.Abs(previous.SceneIntensity - current.SceneIntensity) > 0.0001 ||
        !string.Equals(previous.WallpaperMode, current.WallpaperMode, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.WallpaperPath, current.WallpaperPath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.WallpaperFit, current.WallpaperFit, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(previous.AccentHex, current.AccentHex, StringComparison.OrdinalIgnoreCase);

    private void MaintainExplorerAttachment()
    {
        if (_windowHandle == IntPtr.Zero) return;

        if (ExplorerDesktopHost.IsAttached(_windowHandle))
        {
            var healthy = ExplorerDesktopHost.ResizeToDesktopRect(
                _windowHandle,
                _monitor.Left,
                _monitor.Top,
                _monitor.Width,
                _monitor.Height);
            if (healthy)
            {
                var becameHealthy = !_attached;
                _attached = true;
                if (!IsVisible) Show();
                if (becameHealthy) RequestFreshRender();
                return;
            }

            _attached = false;
            ReportProbe();
        }

        _attached = ExplorerDesktopHost.TryAttach(
            _windowHandle,
            _monitor.Left,
            _monitor.Top,
            _monitor.Width,
            _monitor.Height);

        if (_attached)
        {
            if (!IsVisible) Show();
            RequestFreshRender();
        }
        else if (IsVisible)
        {
            Hide();
        }

        ReportProbe();
    }

    private void RequestFreshRender()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SceneRoot.InvalidateMeasure();
            SceneRoot.InvalidateArrange();
            SceneRoot.InvalidateVisual();
            Renderer.InvalidateMeasure();
            Renderer.InvalidateArrange();
            Renderer.InvalidateVisual();
        }), DispatcherPriority.Render);
    }

    private async Task ApplyPlaybackPolicyAsync(bool forceProfileRefresh)
    {
        var directive = _ruleEngine.Evaluate(_playbackSettings);
        var policyChanged = directive.Action != _lastPolicyAction ||
                            !string.Equals(directive.TargetId, _lastPolicyTarget, StringComparison.OrdinalIgnoreCase);
        _lastPolicyAction = directive.Action;
        _lastPolicyTarget = directive.TargetId;

        switch (directive.Action)
        {
            case ApplicationRuleAction.Stop:
                if (!Renderer.IsStopped) Renderer.Stop();
                return;
            case ApplicationRuleAction.Pause:
                if (Renderer.IsStopped) await RefreshBaseSceneAsync(force: true);
                Renderer.Pause();
                return;
            case ApplicationRuleAction.LoadScene:
                if (!string.IsNullOrWhiteSpace(directive.TargetId))
                    await LoadSceneByIdAsync(directive.TargetId, force: policyChanged);
                return;
            case ApplicationRuleAction.LoadPlaylist:
                if (!string.IsNullOrWhiteSpace(directive.TargetId))
                    await PlayPlaylistAsync(directive.TargetId, force: policyChanged);
                return;
            case ApplicationRuleAction.LoadProfile:
                if (!string.IsNullOrWhiteSpace(directive.TargetId))
                    await PlayProfileAsync(directive.TargetId, force: policyChanged || forceProfileRefresh);
                return;
            default:
                if (Renderer.IsStopped) await RefreshBaseSceneAsync(force: true);
                if (Renderer.IsPaused) Renderer.Resume();
                break;
        }

        if (!string.IsNullOrWhiteSpace(_playbackSettings.ActiveProfileId))
        {
            await PlayProfileAsync(_playbackSettings.ActiveProfileId, force: forceProfileRefresh);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_playbackSettings.ActivePlaylistId))
        {
            await PlayPlaylistAsync(_playbackSettings.ActivePlaylistId, force: false);
            return;
        }

        await RefreshBaseSceneAsync(force: forceProfileRefresh);
    }

    private Task RefreshBaseSceneAsync(bool force) =>
        LoadSceneByIdAsync(_settings.Appearance.SceneId, force);

    private async Task LoadSceneByIdAsync(string? sceneId, bool force)
    {
        var scene = _sceneCatalog.Find(sceneId) ?? _sceneCatalog.Find("builtin:aurora");
        if (scene is null) return;
        if (!force && string.Equals(_loadedSceneId, scene.Id, StringComparison.OrdinalIgnoreCase) && !Renderer.IsStopped)
            return;

        var version = ++_sceneLoadVersion;
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _sceneLoadCancellation, cancellation);
        try { previousCancellation?.Cancel(); } catch (ObjectDisposedException) { }

        var gateEntered = false;
        try
        {
            await _sceneLoadGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != _sceneLoadVersion) return;

            await Renderer.LoadAsync(scene, _settings.Appearance, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != _sceneLoadVersion) return;

            _loadedSceneId = scene.Id;
            Renderer.SetVolume(
                _playbackSettings.GlobalVolume,
                scene.Muted || _playbackSettings.GlobalVolume <= 0);
            RequestFreshRender();
            ReportProbe();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (version != _sceneLoadVersion) return;
            ReportProbe();
            ShellNotificationService.Publish(
                "无法加载桌面场景",
                $"{MonitorLabel}: {error.Message}",
                "warning");
        }
        finally
        {
            if (gateEntered) _sceneLoadGate.Release();
            _ = Interlocked.CompareExchange(ref _sceneLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task PlayPlaylistAsync(string playlistId, bool force)
    {
        var playlist = _playbackSettings.Playlists.FirstOrDefault(item =>
            string.Equals(item.Id, playlistId, StringComparison.OrdinalIgnoreCase));
        if (playlist is null || playlist.SceneIds.Count == 0) return;

        var interval = TimeSpan.FromMinutes(Math.Clamp(playlist.IntervalMinutes, 1, 24 * 60));
        var due = DateTimeOffset.UtcNow - _playlistChangedAt >= interval;
        if (force)
        {
            _playlistIndex = 0;
            _playlistChangedAt = DateTimeOffset.UtcNow;
        }
        else if (due && (!Renderer.IsPaused || playlist.ChangeWhilePaused))
        {
            _playlistIndex = playlist.Shuffle
                ? Random.Shared.Next(playlist.SceneIds.Count)
                : (_playlistIndex + 1) % playlist.SceneIds.Count;
            _playlistChangedAt = DateTimeOffset.UtcNow;
        }

        _playlistIndex = Math.Clamp(_playlistIndex, 0, playlist.SceneIds.Count - 1);
        await LoadSceneByIdAsync(playlist.SceneIds[_playlistIndex], force || due);
    }

    private async Task PlayProfileAsync(string profileId, bool force)
    {
        var profile = _playbackSettings.Profiles.FirstOrDefault(item =>
            string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;

        var assignment = profile.Monitors.FirstOrDefault(item =>
                             string.Equals(item.MonitorKey, _monitor.Id, StringComparison.OrdinalIgnoreCase))
                         ?? (_monitor.IsPrimary
                             ? profile.Monitors.FirstOrDefault(item =>
                                 string.Equals(item.MonitorKey, "primary", StringComparison.OrdinalIgnoreCase))
                             : null);
        if (assignment is null) return;

        if (!string.IsNullOrWhiteSpace(assignment.PlaylistId))
            await PlayPlaylistAsync(assignment.PlaylistId, force);
        else if (!string.IsNullOrWhiteSpace(assignment.SceneId))
            await LoadSceneByIdAsync(assignment.SceneId, force);
    }

    private void ReportProbe()
    {
        DesktopEngineProbe.Report(
            _monitor,
            _attached,
            ExplorerDesktopHost.DescribeAttachment(_windowHandle),
            _loadedSceneId ?? _settings.Appearance.SceneId);
    }
}
