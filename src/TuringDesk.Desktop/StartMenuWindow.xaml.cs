using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class StartMenuWindow : Window
{
    private readonly MainWindow _controlCenter;
    private readonly AppLauncher _launcher = new();
    private readonly DisplayMonitor _monitor;
    private readonly ShellSettingsStore _settingsStore = new();
    private IReadOnlyList<StartAppItem> _allApps = Array.Empty<StartAppItem>();
    private bool _catalogLoaded;

    public ObservableCollection<StartAppItem> VisibleApps { get; } = new();

    public StartMenuWindow(MainWindow controlCenter, DisplayMonitor monitor)
    {
        _controlCenter = controlCenter;
        _monitor = monitor;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        Deactivated += (_, _) => Hide();
    }

    internal async Task ShowMenuAsync()
    {
        if (!IsVisible) Show();
        PositionWindow();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();

        if (!_catalogLoaded)
        {
            await LoadCatalogAsync();
        }
    }

    internal void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        _ = ShowMenuAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UserNameText.Text = Environment.UserName;
        MonitorText.Text = _monitor.IsPrimary
            ? "主显示器 · 当前 Shell 会话"
            : $"扩展显示器 · {_monitor.Width}×{_monitor.Height}";
        PositionWindow();
    }

    private async Task LoadCatalogAsync()
    {
        AppCountText.Text = "正在建立应用索引…";
        var apps = await Task.Run(ShellSurfaceCatalog.LoadStartApps);
        _allApps = apps;
        _catalogLoaded = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        IEnumerable<StartAppItem> result = _allApps;

        if (!string.IsNullOrWhiteSpace(query))
        {
            result = result.Where(item =>
                item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var visible = result.Take(180).ToArray();
        VisibleApps.Clear();
        foreach (var item in visible) VisibleApps.Add(item);

        AppCountText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{_allApps.Count} 个入口"
            : $"{visible.Length} 个结果";
    }

    private void PositionWindow()
    {
        var current = DisplayManager.GetMonitors().FirstOrDefault(monitor => monitor.Id == _monitor.Id) ?? _monitor;
        DisplayManager.PositionPopupBottomCenter(this, current);
    }

    private async Task LaunchPinnedAsync(string app)
    {
        Hide();
        _ = await _launcher.LaunchAsync(app);
    }

    private async void Chrome_Click(object sender, RoutedEventArgs e) => await LaunchPinnedAsync("chrome");
    private async void VSCode_Click(object sender, RoutedEventArgs e) => await LaunchPinnedAsync("code");
    private async void Terminal_Click(object sender, RoutedEventArgs e) => await LaunchPinnedAsync("terminal");

    private void Files_Click(object sender, RoutedEventArgs e) { Hide(); _ = ShellSurfaceCatalog.OpenTarget(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
    private void Settings_Click(object sender, RoutedEventArgs e) { Hide(); _ = ShellSurfaceCatalog.OpenTarget("ms-settings:"); }
    private void Turing_Click(object sender, RoutedEventArgs e) { Hide(); _controlCenter.ShowControlCenter(); }
    private void Desktop_Click(object sender, RoutedEventArgs e) { Hide(); _controlCenter.ShowDesktop(true); }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target })
        {
            Hide();
            _ = ShellSurfaceCatalog.OpenTarget(target);
        }
    }

    private void App_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: StartAppItem item }) return;

        var settings = _settingsStore.Load();
        var existing = settings.PinnedApps.FirstOrDefault(app =>
            string.Equals(app.Target, item.Target, StringComparison.OrdinalIgnoreCase));

        var menu = new ContextMenu();
        var action = new MenuItem
        {
            Header = existing is null ? "固定到任务栏" : "从任务栏取消固定"
        };
        action.Click += (_, _) =>
        {
            if (existing is null)
            {
                settings.PinnedApps.Add(new PinnedShellApp(item.Name, item.Target, item.Target, item.Glyph));
                ShellNotificationService.Publish("已固定到任务栏", item.Name, "shell");
            }
            else
            {
                settings.PinnedApps.RemoveAll(app =>
                    string.Equals(app.Target, item.Target, StringComparison.OrdinalIgnoreCase));
                ShellNotificationService.Publish("已取消固定", item.Name, "shell");
            }
            _settingsStore.Save(settings);
        };
        menu.Items.Add(action);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsList.SelectedItem is StartAppItem item)
        {
            Hide();
            _ = ShellSurfaceCatalog.OpenTarget(item.Target);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_catalogLoaded) ApplyFilter(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }
}
