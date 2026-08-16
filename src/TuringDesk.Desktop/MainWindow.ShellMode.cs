using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private readonly List<ShellBarWindow> _shellBars = [];
    private readonly List<DesktopSurfaceWindow> _desktopSurfaces = [];
    private DispatcherTimer? _displayRefreshTimer;
    private string _displaySignature = string.Empty;

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

        foreach (var surface in _desktopSurfaces)
        {
            surface.Hide();
        }

        if (!IsVisible) Show();
        WindowState = WindowState.Maximized;
        Activate();
        Focus();
    }

    internal void ShowDesktop(bool minimizeWindows)
    {
        if (!ShellSession.IsShellMode) return;

        Hide();
        var first = true;
        foreach (var surface in _desktopSurfaces)
        {
            surface.ShowAsDesktop(minimizeWindows && first);
            first = false;
        }
    }

    internal void ToggleStartMenu()
    {
        var primary = _shellBars.FirstOrDefault(bar => bar.Tag is bool isPrimary && isPrimary) ?? _shellBars.FirstOrDefault();
        primary?.ToggleStartMenu();
    }

    private void ShellMode_Loaded(object sender, RoutedEventArgs e)
    {
        RebuildShellSurfaces(force: true);

        _displayRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _displayRefreshTimer.Tick += (_, _) => RebuildShellSurfaces(force: false);
        _displayRefreshTimer.Start();

        Dispatcher.BeginInvoke(new Action(() => ShowDesktop(false)), DispatcherPriority.ApplicationIdle);
    }

    private void RebuildShellSurfaces(bool force)
    {
        var signature = DisplayManager.GetSignature();
        if (!force && signature == _displaySignature && _desktopSurfaces.Count > 0 && _shellBars.Count > 0) return;
        _displaySignature = signature;

        foreach (var bar in _shellBars.ToArray())
        {
            try { bar.Close(); } catch { }
        }
        _shellBars.Clear();

        foreach (var surface in _desktopSurfaces.ToArray())
        {
            try { surface.Close(); } catch { }
        }
        _desktopSurfaces.Clear();

        var monitors = DisplayManager.GetMonitors();
        foreach (var monitor in monitors)
        {
            var surface = new DesktopSurfaceWindow(this, monitor, showDesktopItems: monitor.IsPrimary);
            _desktopSurfaces.Add(surface);
            surface.ShowAsDesktop(false);
        }

        foreach (var monitor in monitors)
        {
            var bar = new ShellBarWindow(this, monitor) { Tag = monitor.IsPrimary };
            _shellBars.Add(bar);
            bar.Show();
        }
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
        _displayRefreshTimer?.Stop();
        _displayRefreshTimer = null;

        foreach (var bar in _shellBars.ToArray())
        {
            try { bar.Close(); } catch { }
        }
        _shellBars.Clear();

        foreach (var surface in _desktopSurfaces.ToArray())
        {
            try { surface.Close(); } catch { }
        }
        _desktopSurfaces.Clear();
    }
}
