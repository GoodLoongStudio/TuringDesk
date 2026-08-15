using System.Windows;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var shellMode = e.Args.Any(arg => string.Equals(arg, "--shell", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;

        if (shellMode)
        {
            window.EnableShellMode();
        }

        window.Show();
    }
}
