using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DesktopDiagnostics.Initialize(this);
        DesktopDiagnostics.Info("startup.begin", $"args={string.Join(' ', e.Args)}");

        try
        {
            base.OnStartup(e);

            if (e.Args.Any(arg => string.Equals(arg, "--verify-app-search", StringComparison.OrdinalIgnoreCase)))
            {
                DesktopDiagnostics.Info("startup.mode", "verify-app-search");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.BeginInvoke(new Action(RunAppSearchVerification));
                return;
            }

            // Desktop settings and the Harness console are utility surfaces, not
            // full-screen application pages. Size them after per-monitor DPI is known
            // and keep them inside the Windows work area so the taskbar is never
            // covered on 125%/150% scaled desktops.
            CompactToolWindowService.Install();
            DesktopDiagnostics.Info("startup.phase", "compact-tool-window-service-installed");

            // Runtime and DeepSeek Harness are intentionally NOT started here.
            // Enhancement mode, the wallpaper engine and the RAM search index must be
            // usable with the heavy Agent stack completely cold. HarnessConsoleWindow
            // is the explicit boundary that wakes the official workbench.
            var shellMode = e.Args.Any(arg => string.Equals(arg, "--shell", StringComparison.OrdinalIgnoreCase));
            var controlOnly = e.Args.Any(arg => string.Equals(arg, "--control-only", StringComparison.OrdinalIgnoreCase));
            DesktopDiagnostics.Info("startup.mode", shellMode ? "shell" : controlOnly ? "control-only" : "enhancement");

            var window = new MainWindow();
            MainWindow = window;
            DesktopDiagnostics.Info("startup.phase", "main-window-created");

            if (shellMode)
            {
                window.EnableShellMode();
                DesktopDiagnostics.Info("startup.phase", "shell-mode-enabled");
            }
            else if (!controlOnly)
            {
                window.EnableEnhancementMode();
                DesktopDiagnostics.Info("startup.phase", "enhancement-mode-enabled");
            }

            window.Show();
            DesktopDiagnostics.Info("startup.window-shown", "main-window-show-returned");
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Fatal("startup.onstartup", error);
            throw;
        }
    }

    private async void RunAppSearchVerification()
    {
        var exitCode = 1;
        try
        {
            var result = await AppSearchVerification.RunAsync();
            exitCode = result.Success ? 0 : 2;
            DesktopDiagnostics.Info("verify.app-search", $"success={result.Success} exitCode={exitCode}");
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Error("verify.app-search", "verification failed", error);
            exitCode = 3;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }
}
