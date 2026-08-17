using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Start Harness at the earliest safe application bootstrap point. It no
        // longer waits for MainWindow construction, Loaded, or the WebView console.
        // ModelSettingsWindow restarts it after a saved credential/config change.
        _ = StartHarnessAsync();

        var shellMode = e.Args.Any(arg => string.Equals(arg, "--shell", StringComparison.OrdinalIgnoreCase));
        var controlOnly = e.Args.Any(arg => string.Equals(arg, "--control-only", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;

        if (shellMode)
        {
            // Advanced mode: TuringDesk becomes the current user's replacement shell.
            window.EnableShellMode();
        }
        else if (!controlOnly)
        {
            // Default mode: Wallpaper Engine-style integration. Explorer remains
            // the Windows shell while TuringDesk attaches only its scene layer
            // behind Explorer desktop icons and keeps AI services in user space.
            window.EnableEnhancementMode();
        }

        window.Show();
    }

    private static async Task StartHarnessAsync()
    {
        try
        {
            await HarnessWebUiService.EnsureRunningAsync();
        }
        catch (Exception error)
        {
            // Do not prevent Windows/Explorer from starting or remaining usable
            // if Harness has a transient startup/configuration problem.
            ShellNotificationService.Publish(
                "DeepSeek Harness 启动失败",
                error.Message,
                "error");
        }
    }
}
