using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private enum ShellViewState
    {
        Desktop,
        ControlCenter
    }

    private static readonly TimeSpan ShellFadeIn = TimeSpan.FromMilliseconds(155);
    private static readonly TimeSpan ShellFadeOut = TimeSpan.FromMilliseconds(95);

    private readonly List<ShellBarWindow> _shellBars = [];
    private readonly List<DesktopSurfaceWindow> _desktopSurfaces = [];
    private DispatcherTimer? _displayRefreshTimer;
    private string _displaySignature = string.Empty;
    private ShellViewState _shellViewState = ShellViewState.Desktop;
    private int _shellTransitionVersion;

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

        _shellViewState = ShellViewState.ControlCenter;
        var transition = ++_shellTransitionVersion;

        foreach (var surface in _desktopSurfaces)
        {
            surface.HideFromDesktop(animate: true);
        }

        BeginAnimation(OpacityProperty, null);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        WindowState = WindowState.Maximized;
        Activate();
        Focus();

        var fade = new DoubleAnimation
        {
            From = Math.Clamp(Opacity, 0, 1),
            To = 1,
            Duration = new Duration(ShellFadeIn),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (transition != _shellTransitionVersion || _shellViewState != ShellViewState.ControlCenter) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    internal void ShowDesktop(bool minimizeWindows)
    {
        if (!ShellSession.IsShellMode) return;

        _shellViewState = ShellViewState.Desktop;
        var transition = ++_shellTransitionVersion;
        var primary = _desktopSurfaces.FirstOrDefault(surface => surface.IsPrimary) ?? _desktopSurfaces.FirstOrDefault();

        var first = true;
        foreach (var surface in _desktopSurfaces)
        {
            surface.ShowAsDesktop(minimizeWindows && first, activate: false, animate: true);
            first = false;
        }

        void FinishDesktopTransition()
        {
            if (transition != _shellTransitionVersion || _shellViewState != ShellViewState.Desktop) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (IsVisible) Hide();
            primary?.Activate();
            primary?.Focus();
        }

        if (!IsVisible)
        {
            FinishDesktopTransition();
            return;
        }

        BeginAnimation(OpacityProperty, null);
        var fade = new DoubleAnimation
        {
            From = Math.Clamp(Opacity, 0, 1),
            To = 0,
            Duration = new Duration(ShellFadeOut),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => FinishDesktopTransition();
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
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
            if (_shellViewState == ShellViewState.Desktop)
            {
                surface.ShowAsDesktop(minimizeWindows: false, activate: false, animate: false);
            }
        }

        foreach (var monitor in monitors)
        {
            var bar = new ShellBarWindow(this, monitor) { Tag = monitor.IsPrimary };
            _shellBars.Add(bar);
            bar.Show();
        }

        // Display topology changes must preserve the user's current shell view.
        if (_shellViewState == ShellViewState.ControlCenter)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!IsVisible) Show();
            WindowState = WindowState.Maximized;
            Activate();
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
