using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

internal static class DesktopEngineProbe
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DesktopEngineProbeState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string StatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuringDesk",
        "desktop-engine-status.json");

    public static void Report(DisplayMonitor monitor, bool attached, string attachment, string? sceneId)
    {
        lock (Gate)
        {
            States[monitor.Id] = new DesktopEngineProbeState(
                monitor.Id,
                monitor.IsPrimary,
                attached,
                attachment,
                sceneId,
                DateTimeOffset.UtcNow);

            try
            {
                var directory = Path.GetDirectoryName(StatusPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temp = StatusPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(States.Values.OrderByDescending(item => item.IsPrimary), JsonOptions));
                File.Move(temp, StatusPath, true);
            }
            catch
            {
                // Diagnostics must never affect desktop availability.
            }
        }
    }
}

internal sealed record DesktopEngineProbeState(
    string MonitorId,
    bool IsPrimary,
    bool Attached,
    string Attachment,
    string? SceneId,
    DateTimeOffset UpdatedAtUtc);
