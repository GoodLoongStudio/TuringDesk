using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSurfaceWindow : Window
{
    private readonly MainWindow _controlCenter;
    private readonly WindowManager _windows = new();
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<DesktopSurfaceItem> DesktopItems { get; } = new();

    public DesktopSurfaceWindow(MainWindow controlCenter)
    {
        _controlCenter = controlCenter;
        InitializeComponent();
        DataContext = this;

        Loaded += OnLoaded;
        Closed += OnClosed;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _refreshTimer.Tick += (_, _) => RefreshSurface();
    }

    internal void ShowAsDesktop(bool minimizeWindows)
    {
        if (minimizeWindows)
        {
            _windows.MinimizeAll();
        }

        PositionOnPrimaryScreen();
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
        RefreshSurface();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnPrimaryScreen();
        RefreshSurface();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e) => _refreshTimer.Stop();

    private void PositionOnPrimaryScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.PrimaryScreenWidth);
        Height = Math.Max(1, SystemParameters.PrimaryScreenHeight);
    }

    private void RefreshSurface()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy年M月d日 · dddd");

        var selectedPath = (DesktopItemsList.SelectedItem as DesktopSurfaceItem)?.Path;
        var latest = ShellSurfaceCatalog.LoadDesktopItems();

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

    private void DesktopItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DesktopItemsList.SelectedItem is DesktopSurfaceItem item)
        {
            _ = ShellSurfaceCatalog.OpenTarget(item.Path);
        }
    }

    private void ControlCenter_Click(object sender, RoutedEventArgs e) => _controlCenter.ShowControlCenter();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSurface();

    private void OpenDesktopFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _ = ShellSurfaceCatalog.OpenTarget(path);
    }
}
