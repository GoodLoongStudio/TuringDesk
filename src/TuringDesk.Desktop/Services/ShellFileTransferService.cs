using System.IO;

namespace TuringDesk.Desktop.Services;

public sealed record DesktopDropResult(int Copied, int Skipped, int Failed);

public static class ShellFileTransferService
{
    public static async Task<DesktopDropResult> CopyToDesktopAsync(IEnumerable<string> sourcePaths)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(desktop);

        var copied = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var source in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Take(50))
        {
            try
            {
                var fullSource = Path.GetFullPath(source);
                if (!File.Exists(fullSource) && !Directory.Exists(fullSource))
                {
                    skipped++;
                    continue;
                }

                if (IsInsideDirectory(fullSource, desktop))
                {
                    skipped++;
                    continue;
                }

                var name = Path.GetFileName(fullSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name))
                {
                    skipped++;
                    continue;
                }

                var destination = GetUniqueDestination(desktop, name, Directory.Exists(fullSource));
                if (Directory.Exists(fullSource))
                {
                    await Task.Run(() => CopyDirectory(fullSource, destination));
                }
                else
                {
                    await using var sourceStream = File.Open(fullSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await using var destinationStream = File.Create(destination);
                    await sourceStream.CopyToAsync(destinationStream);
                }
                copied++;
            }
            catch
            {
                failed++;
            }
        }

        return new DesktopDropResult(copied, skipped, failed);
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniqueDestination(string directory, string name, bool isDirectory)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        for (var i = 2; i <= 999; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
