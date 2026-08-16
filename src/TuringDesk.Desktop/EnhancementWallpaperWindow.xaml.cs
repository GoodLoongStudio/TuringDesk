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
        Loaded += OnLoaded;
        Closed += OnClosed;

        _hostHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hostHealthTimer.Tick += (_, _) => MaintainExplorerAttachment();
    }

    public bool IsAttached => _attached;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _attached = ExplorerDesktopHost.TryAttach(_windowHandle);

        if (_attached)
        {
            Opacity = 1;
            return;
        }

        // Never leave a failed wallpaper host as a normal full-screen window.
        // If Explorer's desktop seam cannot be found, hide the scene and keep
        // the ordinary Windows desktop completely usable.
        Hide();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShellSettingsStore.SettingsChanged += OnShellSettingsChanged;
        RefreshWallpaper(force: true);
        if (_attached) _hostHealthTimer.Start();
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

    private void MaintainExplorerAttachment()
    {
        if (_windowHandle == IntPtr.Zero) return;

        if (ExplorerDesktopHost.IsAttached(_windowHandle))
        {
            _ = ExplorerDesktopHost.ResizeToVirtualDesktop(_windowHandle);
            return;
        }

        // Explorer can rebuild its WorkerW hierarchy after display topology or
        // shell changes. Re-attach opportunistically without taking focus.
        _attached = ExplorerDesktopHost.TryAttach(_windowHandle);
        if (_attached && !IsVisible)
        {
            Show();
            Opacity = 1;
        }
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
