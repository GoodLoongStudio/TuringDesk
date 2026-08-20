namespace TuringDesk.Desktop;

public partial class MainWindow
{
    internal void ShowDesktopLibrary()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowDesktopLibrary);
            return;
        }

        var window = new DesktopLibraryWindow(_modelStore)
        {
            Owner = IsVisible ? this : null
        };
        window.ShowDialog();
    }
}
