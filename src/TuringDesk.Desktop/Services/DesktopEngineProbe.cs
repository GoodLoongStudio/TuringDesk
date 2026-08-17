using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

internal static class DesktopEngineProbe
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DesktopEngineProbeState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Signatures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string StatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuringDesk",
        "desktop-engine-status.json");

    public static void Report(DisplayMonitor monitor, bool attached, string attachment, string? sceneId)
    {
        var state = new DesktopEngineProbeState(
            monitor.Id,
            monitor.IsPrimary,
            attached,
            attachment,
            sceneId,
            DateTimeOffset.UtcNow);
        var signature = $"{attached}|{attachment}|{sceneId}";
        var changed = false;

        lock (Gate)
        {
            States[monitor.Id] = state;
            changed = !Signatures.TryGetValue(monitor.Id, out var previous) ||
                      !string.Equals(previous, signature, StringComparison.Ordinal);
            Signatures[monitor.Id] = signature;

            try
            {
                var directory = Path.GetDirectoryName(StatusPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temp = StatusPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(States.Values.OrderByDescending(item => item.IsPrimary), JsonOptions));
                File.Move(temp, StatusPath, true);
            }
            catch (Exception error)
            {
                SceneEngineTrace.Error("probe.status-file", $"failed path={StatusPath}", error);
            }
        }

        if (!changed) return;

        SceneEngineTrace.Info(
            "probe.state",
            $"monitor={monitor.Id} primary={monitor.IsPrimary} attached={attached} scene={sceneId ?? "<null>"} attachment={attachment}");

        ExplorerDesktopDiagnostics.Capture(
            $"probe-change monitor={monitor.Id} attached={attached} scene={sceneId ?? "<null>"}");
    }
}

internal sealed record DesktopEngineProbeState(
    string MonitorId,
    bool IsPrimary,
    bool Attached,
    string Attachment,
    string? SceneId,
    DateTimeOffset UpdatedAtUtc);
