using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Harness is part of the TuringDesk desktop runtime, not something the
        // user has to launch by opening the WebView console. Start the official
        // DeepSeek Harness web profile as soon as the desktop is running. The
        // WebView is only a native-looking window onto the already-running UI.
        // Quick chat, voice commands, conversation cards and trace cards remain
        // native TuringDesk surfaces and do not depend on that WebView being open.
        _ = StartHarnessAsync();
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
