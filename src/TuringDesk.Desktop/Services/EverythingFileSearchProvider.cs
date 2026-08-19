using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Zero-index-cost adapter for voidtools Everything.
/// TuringDesk never starts Everything and never owns its database; when the user
/// already has Everything + ES running, file queries are delegated to its IPC-backed
/// command line client. This keeps the desktop process from crawling the file system.
/// </summary>
internal sealed class EverythingFileSearchProvider
{
    private const string EverythingIpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private readonly string? _esPath;

    public EverythingFileSearchProvider() => _esPath = FindEsExecutable();

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_esPath) &&
        File.Exists(_esPath) &&
        FindWindow(EverythingIpcWindowClass, null) != IntPtr.Zero;

    public string ProviderName => IsAvailable ? "Everything" : "本地快速索引";

    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        limit = Math.Clamp(limit, 1, 16);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _esPath!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(limit.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-full-path-and-name");
        process.StartInfo.ArgumentList.Add(query);

        try
        {
            if (!process.Start()) return Array.Empty<string>();

            var readOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var readError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await readOutput.ConfigureAwait(false);
            _ = await readError.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return Array.Empty<string>();

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? FindEsExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "es.exe")
        };

        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe");
        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything 1.5a", "es.exe");
        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "es.exe");

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { candidates.Add(Path.Combine(directory, "es.exe")); } catch { }
            }
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .FirstOrDefault(File.Exists);
    }

    private static void AddIfRooted(List<string> candidates, string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var path = root;
            foreach (var part in parts) path = Path.Combine(path, part);
            candidates.Add(path);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
}
