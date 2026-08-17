using System.IO;

namespace TuringDesk.Desktop.Services;

internal static class SceneEngineTrace
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuringDesk",
        "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "scene-engine.log");

    public static void Info(string category, string message) => Write("INFO", category, message, null);
    public static void Warn(string category, string message) => Write("WARN", category, message, null);
    public static void Error(string category, string message, Exception? error = null) => Write("ERROR", category, message, error);

    private static void Write(string level, string category, string message, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                var cleanMessage = (message ?? string.Empty)
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | pid={Environment.ProcessId} | tid={Environment.CurrentManagedThreadId} | {level} | {category} | {cleanMessage}";
                if (error is not null)
                {
                    var errorText = error.ToString()
                        .Replace("\r", " ", StringComparison.Ordinal)
                        .Replace("\n", " ", StringComparison.Ordinal);
                    line += $" | exception={errorText}";
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never make the desktop unavailable.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath)) return;
        var info = new FileInfo(LogPath);
        if (info.Length < MaxLogBytes) return;

        var previous = LogPath + ".1";
        try
        {
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(LogPath, previous);
        }
        catch
        {
            // If rotation fails, continue appending to the current log.
        }
    }
}
