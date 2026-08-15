using System.ComponentModel;
using System.Windows;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private ShellBarWindow? _shellBar;

    internal void EnableShellMode()
    {
        ShellSession.IsShellMode = true;
        ShellSession.ExitRequested = false;
        Title = "TuringDesk Shell";
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        WindowState = WindowState.Maximized;

        Loaded += ShellMode_Loaded;
        Closing += ShellMode_Closing;
        Closed += ShellMode_Closed;
    }

    private void ShellMode_Loaded(object sender, RoutedEventArgs e)
    {
        _shellBar ??= new ShellBarWindow();
        if (!_shellBar.IsVisible) _shellBar.Show();
    }

    private void ShellMode_Closing(object? sender, CancelEventArgs e)
    {
        if (ShellSession.IsShellMode && !ShellSession.ExitRequested)
        {
            e.Cancel = true;
            WindowState = WindowState.Maximized;
            Activate();
        }
    }

    private void ShellMode_Closed(object? sender, EventArgs e)
    {
        if (_shellBar is not null)
        {
            _shellBar.Close();
            _shellBar = null;
        }
    }
}
