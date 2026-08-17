using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class EnhancementWallpaperWindow : Window
{
    private readonly DisplayMonitor _monitor;
    private readonly ShellSettingsStore _settingsStore = new();
    private readonly SceneCatalogService _sceneCatalog = new();
    private readonly DesktopPlaybackSettingsStore _playbackStore = new();
    private readonly DesktopPlaybackRuleEngine _ruleEngine = new();
    private readonly DispatcherTimer _hostHealthTimer;

    private ShellSettings _settings;
    private DesktopPlaybackSettings _playbackSettings;
    private IntPtr _windowHandle;
    private bool _attached;
    private string? _loadedSceneId;
    private ApplicationRuleAction _lastPolicyAction = ApplicationRuleAction.KeepRunning;
    private string? _lastPolicyTarget;
    private DateTimeOffset _playlistChangedAt = DateTimeOffset.UtcNow;
    private int _playlistIndex;
    private int _sceneLoadVersion;

    public EnhancementWallpaperWindow(DisplayMonitor monitor)
    {
        _monitor = monitor;
        _settings = _settingsStore.Load();
        _playbackSettings = _playbackStore.Load();
        InitializeComponent();

        Left = monitor.Left;
        Top = monitor.Top;
        Width = Math.Max(1, monitor.Width);
        Height = Math.Max(1, monitor.Height);
        Opacity = 0;

        Renderer.PlaybackError += message => ShellNotificationService.Publish("桌面场景播放失败", $"{MonitorLabel}: {message}", "warning");
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        _hostHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hostHealthTimer.Tick += async (_, _) => await MaintainDesktopEngineAsync();
    }

    public bool IsAttached => _attached;
    public string MonitorId => _monitor.Id;
    public string MonitorLabel => _monitor.IsPrimary ? "主显示器" : $"显示器 {_monitor.Id}";

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        ShellSettingsStore.SettingsChanged += OnShellSettingsChanged;

        _attached = ExplorerDesktopHost.TryAttach(_windowHandle, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height);
        if (_attached)
        {
            Opacity = 1;
        }
        else
        {
            Hide();
        }

        _ = RefreshBaseSceneAsync(force: true);
        _hostHealthTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hostHealthTimer.Stop();
        ShellSettingsStore.SettingsChanged -= OnShellSettingsChanged;
        Renderer.Stop();
    }

    private void OnShellSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _settings = _settingsStore.Load();
            // Applying a scene also disables an active playlist/profile. Reload
            // playback state here so the old in-memory policy cannot immediately
            // override the scene the user just selected.
            _playbackSettings = _playbackStore.Load();
            _ = ApplyPlaybackPolicyAsync(forceProfileRefresh: true);
        }), DispatcherPriority.Background);
    }

    private async Task MaintainDesktopEngineAsync()
    {
        MaintainExplorerAttachment();

        // The library/control UI can be opened by another TuringDesk process.
        // Static SettingsChanged events do not cross process boundaries, so the
        // desktop host must also observe the persisted settings. This makes an
        // "Apply to desktop" action deterministic even when a background instance
        // owns the WorkerW wallpaper window.
        var latestSettings = _settingsStore.Load();
        var appearanceChanged = AppearanceRequiresReload(_settings.Appearance, latestSettings.Appearance);
        _settings = latestSettings;
        _playbackSettings = _playbackStore.Load();
        await ApplyPlaybackPolicyAsync(forceProfileRefresh: appearanceChanged);
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
            _ = ExplorerDesktopHost.ResizeToDesktopRect(_windowHandle, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height);
            return;
        }

        _attached = ExplorerDesktopHost.TryAttach(_windowHandle, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height);
        if (_attached && !IsVisible)
        {
            Show();
            Opacity = 1;
        }
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

    private Task RefreshBaseSceneAsync(bool force) => LoadSceneByIdAsync(_settings.Appearance.SceneId, force);

    private async Task LoadSceneByIdAsync(string? sceneId, bool force)
    {
        var scene = _sceneCatalog.Find(sceneId) ?? _sceneCatalog.Find("builtin:aurora");
        if (scene is null) return;
        if (!force && string.Equals(_loadedSceneId, scene.Id, StringComparison.OrdinalIgnoreCase) && !Renderer.IsStopped) return;

        var version = ++_sceneLoadVersion;
        try
        {
            await Renderer.LoadAsync(scene, _settings.Appearance);
            if (version != _sceneLoadVersion) return;
            _loadedSceneId = scene.Id;
            Renderer.SetVolume(_playbackSettings.GlobalVolume, scene.Muted || _playbackSettings.GlobalVolume <= 0);
        }
        catch (Exception error)
        {
            if (version != _sceneLoadVersion) return;
            ShellNotificationService.Publish("无法加载桌面场景", $"{MonitorLabel}: {error.Message}", "warning");
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

        // Match stable monitor id first; "primary" is a portable alias that still
        // follows the user's primary display when hardware/topology changes.
        var assignment = profile.Monitors.FirstOrDefault(item =>
                             string.Equals(item.MonitorKey, _monitor.Id, StringComparison.OrdinalIgnoreCase))
                         ?? (_monitor.IsPrimary
                             ? profile.Monitors.FirstOrDefault(item => string.Equals(item.MonitorKey, "primary", StringComparison.OrdinalIgnoreCase))
                             : null);
        if (assignment is null) return;

        if (!string.IsNullOrWhiteSpace(assignment.PlaylistId))
        {
            await PlayPlaylistAsync(assignment.PlaylistId, force);
        }
        else if (!string.IsNullOrWhiteSpace(assignment.SceneId))
        {
            await LoadSceneByIdAsync(assignment.SceneId, force);
        }
    }
}
