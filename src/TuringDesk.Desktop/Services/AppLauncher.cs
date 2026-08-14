using System.Diagnostics;

namespace TuringDesk.Desktop.Services;

public sealed class AppLauncher
{
    public Task<bool> LaunchAsync(string app)
    {
        var candidates = app.ToLowerInvariant() switch
        {
            "chrome" => new[] { "chrome.exe", "chrome" },
            "code" => new[] { "code.cmd", "code.exe", "code" },
            "terminal" => new[] { "wt.exe", "powershell.exe" },
            _ => new[] { app }
        };

        foreach (var candidate in candidates)
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
                // Try the next known launcher alias.
            }
        }

        return Task.FromResult(false);
    }
}
