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
            monitor.DeviceName,
            monitor.IsPrimary,
            monitor.Left,
            monitor.Top,
            monitor.Width,
            monitor.Height,
            monitor.DpiX,
            monitor.DpiY,
            attached,
            attachment,
            sceneId,
            DateTimeOffset.UtcNow);
        var signature =
            $"{monitor.Left},{monitor.Top},{monitor.Width},{monitor.Height}|dpi={monitor.DpiX}x{monitor.DpiY}|{attached}|{attachment}|{sceneId}";
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
            $"monitor={monitor.Id} device={monitor.DeviceName} primary={monitor.IsPrimary} rect={monitor.Left},{monitor.Top},{monitor.Width}x{monitor.Height} dpi={monitor.DpiX}x{monitor.DpiY} attached={attached} scene={sceneId ?? "<null>"} attachment={attachment}");

        ExplorerDesktopDiagnostics.Capture(
            $"probe-change monitor={monitor.Id} attached={attached} scene={sceneId ?? "<null>"}");
    }

    /// <summary>
    /// Wait until the live desktop host acknowledges that the primary monitor is
    /// attached and has actually loaded the requested scene. This deliberately
    /// reads the status file as well as in-process state because Desktop Library can
    /// be opened by a second TuringDesk process while the enhancement host owns the
    /// wallpaper renderer.
    /// </summary>
    public static async Task<bool> WaitForPrimarySceneAsync(
        string sceneId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return false;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PrimarySceneMatches(sceneId)) return true;
            await Task.Delay(100, cancellationToken);
        }

        return PrimarySceneMatches(sceneId);
    }

    private static bool PrimarySceneMatches(string sceneId)
    {
        lock (Gate)
        {
            var primary = States.Values.FirstOrDefault(state => state.IsPrimary);
            if (Matches(primary, sceneId)) return true;
        }

        try
        {
            if (!File.Exists(StatusPath)) return false;
            var states = JsonSerializer.Deserialize<List<DesktopEngineProbeState>>(
                File.ReadAllText(StatusPath),
                JsonOptions);
            var primary = states?.FirstOrDefault(state => state.IsPrimary);
            return Matches(primary, sceneId);
        }
        catch
        {
            // The host writes through a temporary file and atomic move, but a second
            // process may still observe a transient sharing/replace race. The caller
            // retries until timeout instead of treating one read as a hard failure.
            return false;
        }
    }

    private static bool Matches(DesktopEngineProbeState? state, string sceneId) =>
        state is not null &&
        state.Attached &&
        string.Equals(state.SceneId, sceneId, StringComparison.OrdinalIgnoreCase) &&
        DateTimeOffset.UtcNow - state.UpdatedAtUtc < TimeSpan.FromSeconds(5);
}

internal sealed record DesktopEngineProbeState(
    string MonitorId,
    string DeviceName,
    bool IsPrimary,
    int Left,
    int Top,
    int Width,
    int Height,
    uint DpiX,
    uint DpiY,
    bool Attached,
    string Attachment,
    string? SceneId,
    DateTimeOffset UpdatedAtUtc);