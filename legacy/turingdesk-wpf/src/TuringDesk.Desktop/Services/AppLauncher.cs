using System.Diagnostics;
using System.IO;

namespace TuringDesk.Desktop.Services;

public sealed class AppLauncher
{
    public Task<bool> LaunchAsync(string app)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = app.ToLowerInvariant() switch
        {
            "chrome" => new[]
            {
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                "chrome.exe"
            },
            "code" => new[]
            {
                Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
                "code.cmd",
                "code.exe"
            },
            "terminal" => new[] { "wt.exe", "powershell.exe" },
            _ => new[] { app }
        };

        foreach (var candidate in candidates.Where(x => !Path.IsPathFullyQualified(x) || File.Exists(x)))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    UseShellExecute = true
                });
                return Task.FromResult(true);
            }
            catch
            {
                // Try the next known launcher path/alias.
            }
        }

        return Task.FromResult(false);
    }
}
