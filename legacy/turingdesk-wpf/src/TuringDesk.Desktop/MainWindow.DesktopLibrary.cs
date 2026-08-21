namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private DesktopLibraryWindow? _desktopLibraryWindow;

    internal void ShowDesktopLibrary()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowDesktopLibrary);
            return;
        }

        if (_desktopLibraryWindow is { IsVisible: true } existing)
        {
            if (existing.WindowState == System.Windows.WindowState.Minimized)
                existing.WindowState = System.Windows.WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new DesktopLibraryWindow(_modelStore);
        _desktopLibraryWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_desktopLibraryWindow, window))
                _desktopLibraryWindow = null;
        };
        window.Show();
        window.Activate();
    }
}
