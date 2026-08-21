using System.Windows;
using System.Windows.Input;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly ModelSettingsStore _modelStore = new();

    public MainWindow()
    {
        ShellThemeService.Apply(new ShellSettingsStore().Load().Appearance);
        InitializeComponent();

        UnifiedModelConfigurationService.ConfigurationChanged += OnModelConfigurationChanged;
        Closed += (_, _) => UnifiedModelConfigurationService.ConfigurationChanged -= OnModelConfigurationChanged;
    }

    private void OnModelConfigurationChanged(ModelSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnModelConfigurationChanged(settings)));
            return;
        }

        DesktopDiagnostics.Info(
            "model.configuration-changed",
            $"provider={settings.ProviderId} model={settings.Model}");
        _desktopSearchBar?.ReloadModelChoicesFromConfiguration();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
