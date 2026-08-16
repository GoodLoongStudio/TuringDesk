using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSurfaceWindow : Window
{
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(145);
    private static readonly TimeSpan RefreshFreshness = TimeSpan.FromSeconds(3);

    private readonly MainWindow _controlCenter;
    private readonly WindowManager _windows = new();
    private readonly ShellSettingsStore _settingsStore = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DisplayMonitor _monitor;
    private readonly bool _showDesktopItems;
    private readonly Brush _fallbackBackground;
    private ShellSettings _settings;
    private bool _refreshing;
    private string? _wallpaperSignature;
    private Point _dragStart;
    private string? _dragPath;
    private DateTimeOffset _lastSurfaceRefresh = DateTimeOffset.MinValue;
    private int _transitionVersion;

    public ObservableCollection<DesktopSurfaceItem> DesktopItems { get; } = new();

    public DesktopSurfaceWindow(MainWindow controlCenter, DisplayMonitor monitor, bool showDesktopItems)
    {
        _controlCenter = controlCenter;
        _monitor = monitor;
        _showDesktopItems = showDesktopItems;
        _settings = _settingsStore.Load();

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

    internal bool IsPrimary => _monitor.IsPrimary;

    internal void ShowAsDesktop(bool minimizeWindows, bool activate, bool animate = true)
    {
        if (minimizeWindows)
        {
            _windows.MinimizeAll();
        }

        var transition = ++_transitionVersion;
        BeginAnimation(OpacityProperty, null);
        PositionOnMonitor();
        WindowState = WindowState.Normal;

        if (!IsVisible)
        {
            Opacity = animate ? 0 : 1;
            Show();
        }

        if (animate)
        {
            var fade = new DoubleAnimation
            {
                From = Math.Clamp(Opacity, 0, 1),
                To = 1,
                Duration = new Duration(TransitionDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            Opacity = 1;
        }

        if (activate)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (transition != _transitionVersion || !IsVisible) return;
                Activate();
                Focus();
            }), DispatcherPriority.Input);
        }

        if (DateTimeOffset.UtcNow - _lastSurfaceRefresh > RefreshFreshness)
        {
            _ = RefreshSurfaceAsync();
        }
    }

    internal void HideFromDesktop(bool animate = true)
    {
        if (!IsVisible) return;
        var transition = ++_transitionVersion;
        BeginAnimation(OpacityProperty, null);

        if (!animate)
        {
            Opacity = 1;
            Hide();
            return;
        }

        var fade = new DoubleAnimation
        {
            From = Math.Clamp(Opacity, 0, 1),
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(95)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (transition != _transitionVersion) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Hide();
        };
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShellSettingsStore.SettingsChanged += OnShellSettingsChanged;
        ShellThemeService.Apply(_settings.Appearance);
        PositionOnMonitor();
        await RefreshSurfaceAsync();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        ShellSettingsStore.SettingsChanged -= OnShellSettingsChanged;
    }

    private void OnShellSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _settings = _settingsStore.Load();
            ShellThemeService.Apply(_settings.Appearance);
            _wallpaperSignature = null;
            RefreshWallpaper();
        }), DispatcherPriority.Background);
    }

    private void PositionOnMonitor() => DisplayManager.PositionWindow(this, _monitor);

    private async Task RefreshSurfaceAsync()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy年M月d日 · dddd");
        RefreshWallpaper();

        if (!_showDesktopItems || _refreshing)
        {
            _lastSurfaceRefresh = DateTimeOffset.UtcNow;
            return;
        }
        _refreshing = true;

        try
        {
            var selectedPath = (DesktopItemsList.SelectedItem as DesktopSurfaceItem)?.Path;
            var latest = await Task.Run(ShellSurfaceCatalog.LoadDesktopItems);

            if (DesktopItems.Count != latest.Count ||
                !DesktopItems.Select(item => item.Path).SequenceEqual(latest.Select(item => item.Path), StringComparer.OrdinalIgnoreCase))
            {
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

            _lastSurfaceRefresh = DateTimeOffset.UtcNow;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshWallpaper()
    {
        var appearance = _settings.Appearance;
        var resolvedPath = WallpaperService.ResolveWallpaperPath(appearance) ?? string.Empty;
        var signature = $"{appearance.WallpaperMode}|{appearance.WallpaperFit}|{resolvedPath}";
        if (string.Equals(signature, _wallpaperSignature, StringComparison.OrdinalIgnoreCase)) return;

        _wallpaperSignature = signature;
        DesktopRoot.Background = WallpaperService.CreateWallpaperBrush(appearance) ?? _fallbackBackground;
    }

    private void DesktopItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DesktopItemsList.SelectedItem is DesktopSurfaceItem item)
        {
            _ = ShellSurfaceCatalog.OpenTarget(item.Path);
        }
    }

    private void DesktopItemsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not DesktopSurfaceItem item) return;
        DesktopItemsList.SelectedItem = item;

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => _ = ShellSurfaceCatalog.OpenTarget(item.Path);
        menu.Items.Add(open);

        var location = new MenuItem { Header = "打开文件所在位置" };
        location.Click += (_, _) => _ = ShellSurfaceCatalog.OpenContainingFolder(item.Path);
        menu.Items.Add(location);

        menu.Items.Add(new Separator());
        var properties = new MenuItem { Header = "属性" };
        properties.Click += (_, _) => _ = ShellSurfaceCatalog.ShowProperties(item.Path);
        menu.Items.Add(properties);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void DesktopItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(DesktopItemsList);
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is DesktopSurfaceItem item)
        {
            DesktopItemsList.SelectedItem = item;
            _dragPath = item.Path;
        }
        else
        {
            _dragPath = null;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void DesktopItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrWhiteSpace(_dragPath)) return;
        var current = e.GetPosition(DesktopItemsList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var path = _dragPath;
        _dragPath = null;
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        _ = DragDrop.DoDragDrop(DesktopItemsList, data, DragDropEffects.Copy);
    }

    private void Desktop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _showDesktopItems && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Desktop_Drop(object sender, DragEventArgs e)
    {
        if (!_showDesktopItems || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var result = await ShellFileTransferService.CopyToDesktopAsync(paths);
        await RefreshSurfaceAsync();

        var summary = $"复制 {result.Copied} 项";
        if (result.Skipped > 0) summary += $"，跳过 {result.Skipped} 项";
        if (result.Failed > 0) summary += $"，失败 {result.Failed} 项";
        ShellNotificationService.Publish("桌面文件投放完成", summary, result.Failed > 0 ? "warning" : "shell");
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
            ShellNotificationService.Publish("已创建桌面文件夹", Path.GetFileName(candidate), "shell");
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