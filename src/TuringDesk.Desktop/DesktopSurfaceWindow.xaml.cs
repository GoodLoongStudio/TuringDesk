using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSurfaceWindow : Window
{
    private readonly MainWindow _controlCenter;
    private readonly WindowManager _windows = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DisplayMonitor _monitor;
    private readonly bool _showDesktopItems;
    private readonly Brush _fallbackBackground;
    private bool _refreshing;
    private string? _wallpaperPath;

    public ObservableCollection<DesktopSurfaceItem> DesktopItems { get; } = new();

    public DesktopSurfaceWindow(MainWindow controlCenter, DisplayMonitor monitor, bool showDesktopItems)
    {
        _controlCenter = controlCenter;
        _monitor = monitor;
        _showDesktopItems = showDesktopItems;

        InitializeComponent();
        DataContext = this;
        _fallbackBackground = DesktopRoot.Background;

        DesktopItemsList.Visibility = showDesktopItems ? Visibility.Visible : Visibility.Collapsed;
        DesktopHint.Visibility = showDesktopItems ? Visibility.Visible : Visibility.Collapsed;
        ControlCenterButton.Visibility = monitor.IsPrimary ? Visibility.Visible : Visibility.Collapsed;
        MonitorLabelText.Text = monitor.IsPrimary
            ? "AI Native Desktop for Windows · 主显示器"
            : $"扩展桌面 · {monitor.Width}×{monitor.Height}";

        SourceInitialized += (_, _) => PositionOnMonitor();
        Loaded += OnLoaded;
        Closed += OnClosed;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _refreshTimer.Tick += async (_, _) => await RefreshSurfaceAsync();
    }

    internal void ShowAsDesktop(bool minimizeWindows)
    {
        if (minimizeWindows)
        {
            _windows.MinimizeAll();
        }

        if (!IsVisible) Show();
        PositionOnMonitor();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
        _ = RefreshSurfaceAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnMonitor();
        await RefreshSurfaceAsync();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _refreshTimer.Stop();

    private void PositionOnMonitor() => DisplayManager.PositionWindow(this, _monitor);

    private async Task RefreshSurfaceAsync()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy年M月d日 · dddd");
        RefreshWallpaper();

        if (!_showDesktopItems || _refreshing) return;
        _refreshing = true;

        try
        {
            var selectedPath = (DesktopItemsList.SelectedItem as DesktopSurfaceItem)?.Path;
            var latest = await Task.Run(ShellSurfaceCatalog.LoadDesktopItems);

            if (DesktopItems.Count == latest.Count &&
                DesktopItems.Select(item => item.Path).SequenceEqual(latest.Select(item => item.Path), StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            DesktopItems.Clear();
            foreach (var item in latest)
            {
                DesktopItems.Add(item);
            }

            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                DesktopItemsList.SelectedItem = DesktopItems.FirstOrDefault(item =>
                    string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshWallpaper()
    {
        var path = WallpaperService.GetCurrentWallpaperPath();
        if (string.Equals(path, _wallpaperPath, StringComparison.OrdinalIgnoreCase)) return;

        _wallpaperPath = path;
        DesktopRoot.Background = WallpaperService.CreateCurrentWallpaperBrush() ?? _fallbackBackground;
    }

    private void DesktopItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DesktopItemsList.SelectedItem is DesktopSurfaceItem item)
        {
            _ = ShellSurfaceCatalog.OpenTarget(item.Path);
        }
    }

    private void ControlCenter_Click(object sender, RoutedEventArgs e) => _controlCenter.ShowControlCenter();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSurfaceAsync();

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var candidate = Path.Combine(desktop, "新建文件夹");
        var suffix = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(desktop, $"新建文件夹 ({suffix++})");
        }

        try
        {
            Directory.CreateDirectory(candidate);
            await RefreshSurfaceAsync();
        }
        catch
        {
            MessageBox.Show("无法在桌面创建文件夹。", "TuringDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDesktopFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _ = ShellSurfaceCatalog.OpenTarget(path);
    }

    private void DisplaySettings_Click(object sender, RoutedEventArgs e) =>
        _ = ShellSurfaceCatalog.OpenTarget("ms-settings:display");

    private void Personalize_Click(object sender, RoutedEventArgs e) =>
        _ = ShellSurfaceCatalog.OpenTarget("ms-settings:personalization-background");
}
