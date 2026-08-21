using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class App : Application
{
    private SystemTrayService? _tray;

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

            CompactToolWindowService.Install();
            DesktopDiagnostics.Info("startup.phase", "compact-tool-window-service-installed");

            // Runtime and DeepSeek Harness are intentionally NOT started here.
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

            // Native tray icon: no WinForms dependency and no background worker.
            _tray = new SystemTrayService(
                Dispatcher,
                window.ShowDesktopSearchFromTray,
                window.ShowDesktopLibrary,
                window.RequestApplicationExit);
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Fatal("startup.onstartup", error);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopDiagnostics.Info("shutdown.begin", $"exitCode={e.ApplicationExitCode}");
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        base.OnExit(e);
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
