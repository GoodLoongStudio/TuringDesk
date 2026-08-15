using Microsoft.Win32;
using System.Diagnostics;

namespace TuringDesk.ShellHost;

internal static class Program
{
    private const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string StatePath = @"Software\TuringDesk\Shell";
    private const int RestoreExplorerExitCode = 20;

    [STAThread]
    private static int Main(string[] args)
    {
        var preview = args.Any(arg => string.Equals(arg, "--preview", StringComparison.OrdinalIgnoreCase));
        var packageRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
            ?? AppContext.BaseDirectory;

        var runtimeExe = Path.Combine(packageRoot, "runtime", "node", "node.exe");
        var runtimeEntry = Path.Combine(packageRoot, "runtime", "app", "server.js");
        var desktopExe = Path.Combine(packageRoot, "desktop", "TuringDesk.Desktop.exe");

        foreach (var required in new[] { runtimeExe, runtimeEntry, desktopExe })
        {
            if (!File.Exists(required))
            {
                return FailSafe(preview, $"Required shell component is missing: {required}");
            }
        }

        Process? runtime = null;
        try
        {
            runtime = StartRuntime(runtimeExe, runtimeEntry, packageRoot);
            Thread.Sleep(900);
            if (runtime.HasExited)
            {
                return FailSafe(preview, "TuringDesk Runtime exited before the desktop shell started.");
            }

            var consecutiveFailures = 0;
            while (true)
            {
                var startedAt = DateTime.UtcNow;
                var desktop = StartDesktop(desktopExe, packageRoot);
                desktop.WaitForExit();

                if (desktop.ExitCode == RestoreExplorerExitCode)
                {
                    Log("Desktop requested Explorer restoration.");
                    if (!preview) RestoreExplorerPolicy();
                    StartExplorer();
                    return 0;
                }

                if (preview)
                {
                    return desktop.ExitCode;
                }

                var livedFor = DateTime.UtcNow - startedAt;
                consecutiveFailures = livedFor >= TimeSpan.FromMinutes(2) ? 1 : consecutiveFailures + 1;
                Log($"Desktop shell exited with code {desktop.ExitCode} after {livedFor}. Failure count: {consecutiveFailures}.");

                if (consecutiveFailures >= 3)
                {
                    return FailSafe(false, "TuringDesk shell failed repeatedly. Explorer was restored automatically.");
                }

                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, consecutiveFailures))));
            }
        }
        catch (Exception error)
        {
            return FailSafe(preview, error.ToString());
        }
        finally
        {
            try
            {
                if (runtime is { HasExited: false }) runtime.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static Process StartRuntime(string runtimeExe, string runtimeEntry, string packageRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimeExe,
            Arguments = $"\"{runtimeEntry}\"",
            WorkingDirectory = Path.Combine(packageRoot, "runtime"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["TURINGDESK_RUNTIME_MODE"] = "mock";
        Log("Starting embedded TuringDesk Runtime.");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start TuringDesk Runtime.");
    }

    private static Process StartDesktop(string desktopExe, string packageRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = desktopExe,
            Arguments = "--shell",
            WorkingDirectory = Path.Combine(packageRoot, "desktop"),
            UseShellExecute = true
        };
        Log("Starting TuringDesk Desktop in shell mode.");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start TuringDesk Desktop.");
    }

    private static int FailSafe(bool preview, string reason)
    {
        Log($"Fail-safe triggered. preview={preview}. {reason}");
        if (!preview)
        {
            RestoreExplorerPolicy();
            StartExplorer();
        }
        return 1;
    }

    private static void RestoreExplorerPolicy()
    {
        try
        {
            using var state = Registry.CurrentUser.CreateSubKey(StatePath);
            var previousShell = state?.GetValue("PreviousShell") as string;

            using var policy = Registry.CurrentUser.CreateSubKey(PolicyPath);
            var currentShell = policy?.GetValue("Shell") as string;
            if (policy is null) return;

            if (!string.IsNullOrWhiteSpace(currentShell) &&
                currentShell.Contains("TuringDesk.ShellHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(previousShell))
                {
                    policy.DeleteValue("Shell", throwOnMissingValue: false);
                }
                else
                {
                    policy.SetValue("Shell", previousShell, RegistryValueKind.String);
                }
            }

            state?.SetValue("Enabled", 0, RegistryValueKind.DWord);
        }
        catch (Exception error)
        {
            Log($"Could not restore CustomShell policy: {error}");
        }
    }

    private static void StartExplorer()
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(windows, "explorer.exe"),
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            Log($"Could not start Explorer: {error}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TuringDesk",
                "Shell");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "shellhost.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never stop shell recovery.
        }
    }
}
