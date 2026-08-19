using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--verify-app-search", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(new Action(RunAppSearchVerification));
            return;
        }

        // Desktop settings and the Harness console are utility surfaces, not
        // full-screen application pages. Size them after per-monitor DPI is known
        // and keep them inside the Windows work area so the taskbar is never
        // covered on 125%/150% scaled desktops.
        CompactToolWindowService.Install();

        // Runtime and DeepSeek Harness are intentionally NOT started here.
        // Enhancement mode, the wallpaper engine and the RAM search index must be
        // usable with the heavy Agent stack completely cold. HarnessConsoleWindow,
        // an Agent request or an explicit MCP flow is responsible for waking it.
        var shellMode = e.Args.Any(arg => string.Equals(arg, "--shell", StringComparison.OrdinalIgnoreCase));
        var controlOnly = e.Args.Any(arg => string.Equals(arg, "--control-only", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;

        if (shellMode)
        {
            window.EnableShellMode();
        }
        else if (!controlOnly)
        {
            window.EnableEnhancementMode();
        }

        window.Show();
    }

    private async void RunAppSearchVerification()
    {
        var exitCode = 1;
        try
        {
            var result = await AppSearchVerification.RunAsync();
            exitCode = result.Success ? 0 : 2;
        }
        catch
        {
            exitCode = 3;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }
}
