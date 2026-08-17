using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Harness is a core desktop service, not an advanced-console feature.
        // Start its process before constructing MainWindow so the Agent kernel,
        // Models page and Windows MCP boot in parallel with the visible desktop.
        // StartEarly() reaches Process.Start synchronously and returns only the
        // readiness task, so this does not make the Windows shell wait 20 seconds.
        Task<Uri> harnessStartup;
        try
        {
            harnessStartup = HarnessWebUiService.StartEarly();
        }
        catch (Exception error)
        {
            harnessStartup = Task.FromException<Uri>(error);
        }

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
        _ = ObserveHarnessStartupAsync(harnessStartup);
    }

    private static async Task ObserveHarnessStartupAsync(Task<Uri> startup)
    {
        try
        {
            _ = await startup;
        }
        catch (Exception error)
        {
            // Never block Explorer/TuringDesk desktop availability on a transient
            // Agent service failure, but surface the actual startup error.
            ShellNotificationService.Publish(
                "DeepSeek Harness 启动失败",
                error.Message,
                "error");
        }
    }
}
