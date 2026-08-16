using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class EnhancementWallpaperWindow : Window
{
    private readonly ShellSettingsStore _settingsStore = new();
    private readonly DispatcherTimer _hostHealthTimer;
    private ShellSettings _settings;
    private Brush? _fallbackBackground;
    private string? _wallpaperSignature;
    private IntPtr _windowHandle;
    private bool _attached;
    private bool _scenePaused;

    public EnhancementWallpaperWindow()
    {
        _settings = _settingsStore.Load();
        InitializeComponent();
        _fallbackBackground = WallpaperLayer.Background;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1, SystemParameters.VirtualScreenHeight);
        Opacity = 0;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        _hostHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hostHealthTimer.Tick += (_, _) => MaintainScene();
    }

    public bool IsAttached => _attached;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        ShellSettingsStore.SettingsChanged += OnShellSettingsChanged;
        RefreshWallpaper(force: true);

        _attached = ExplorerDesktopHost.TryAttach(_windowHandle);
        if (_attached)
        {
            Opacity = 1;
        }
        else
        {
            // Never leave a failed wallpaper host as a normal full-screen window.
            // The timer remains alive and can attach later if Explorer is still
            // finishing its desktop hierarchy during user sign-in.
            Hide();
        }

        _hostHealthTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hostHealthTimer.Stop();
        ShellSettingsStore.SettingsChanged -= OnShellSettingsChanged;
    }

    private void OnShellSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _settings = _settingsStore.Load();
            RefreshWallpaper(force: true);
        }), DispatcherPriority.Background);
    }

    private void MaintainScene()
    {
        MaintainExplorerAttachment();
        ApplyPerformancePolicy();
    }

    private void MaintainExplorerAttachment()
    {
        if (_windowHandle == IntPtr.Zero) return;

        if (ExplorerDesktopHost.IsAttached(_windowHandle))
        {
            _ = ExplorerDesktopHost.ResizeToVirtualDesktop(_windowHandle);
            return;
        }

        // Explorer can rebuild its WorkerW hierarchy after display topology,
        // Explorer restart, or sign-in initialization. Re-attach without focus.
        _attached = ExplorerDesktopHost.TryAttach(_windowHandle);
        if (_attached && !IsVisible)
        {
            Show();
            Opacity = 1;
        }
    }

    private void ApplyPerformancePolicy()
    {
        var shouldPause = DesktopScenePerformancePolicy.ShouldPauseVisualScene();
        if (shouldPause == _scenePaused) return;

        _scenePaused = shouldPause;
        AmbientSceneLayer.Visibility = shouldPause ? Visibility.Hidden : Visibility.Visible;
    }

    private void RefreshWallpaper(bool force)
    {
        var appearance = _settings.Appearance;
        var resolvedPath = WallpaperService.ResolveWallpaperPath(appearance) ?? string.Empty;
        var signature = $"{appearance.WallpaperMode}|{appearance.WallpaperFit}|{resolvedPath}";
        if (!force && string.Equals(signature, _wallpaperSignature, StringComparison.OrdinalIgnoreCase)) return;

        _wallpaperSignature = signature;
        WallpaperLayer.Background = WallpaperService.CreateWallpaperBrush(appearance) ?? _fallbackBackground;
    }
}
