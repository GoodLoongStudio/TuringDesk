using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private ShellBarWindow? _shellBar;
    private DesktopSurfaceWindow? _desktopSurface;

    internal void EnableShellMode()
    {
        ShellSession.IsShellMode = true;
        ShellSession.ExitRequested = false;
        Title = "TuringDesk Control Center";
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        WindowState = WindowState.Maximized;

        Loaded += ShellMode_Loaded;
        Closing += ShellMode_Closing;
        Closed += ShellMode_Closed;
    }

    internal void ShowControlCenter()
    {
        if (!ShellSession.IsShellMode)
        {
            Show();
            Activate();
            return;
        }

        _desktopSurface?.Hide();
        if (!IsVisible) Show();
        WindowState = WindowState.Maximized;
        Activate();
        Focus();
    }

    internal void ShowDesktop(bool minimizeWindows)
    {
        if (!ShellSession.IsShellMode) return;

        Hide();
        _desktopSurface?.ShowAsDesktop(minimizeWindows);
    }

    private void ShellMode_Loaded(object sender, RoutedEventArgs e)
    {
        _desktopSurface ??= new DesktopSurfaceWindow(this);
        if (!_desktopSurface.IsVisible)
        {
            _desktopSurface.ShowAsDesktop(false);
        }

        _shellBar ??= new ShellBarWindow(this);
        if (!_shellBar.IsVisible)
        {
            _shellBar.Show();
        }

        Dispatcher.BeginInvoke(
            new Action(() => ShowDesktop(false)),
            DispatcherPriority.ApplicationIdle);
    }

    private void ShellMode_Closing(object? sender, CancelEventArgs e)
    {
        if (ShellSession.IsShellMode && !ShellSession.ExitRequested)
        {
            e.Cancel = true;
            ShowDesktop(false);
        }
    }

    private void ShellMode_Closed(object? sender, EventArgs e)
    {
        if (_shellBar is not null)
        {
            _shellBar.Close();
            _shellBar = null;
        }

        if (_desktopSurface is not null)
        {
            _desktopSurface.Close();
            _desktopSurface = null;
        }
    }
}
