using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuringDesk.Desktop.Services.SceneEngine;

public enum PlaybackBehavior
{
    KeepRunning,
    Pause,
    Stop
}

public enum ApplicationRuleCondition
{
    Running,
    Fullscreen
}

public enum ApplicationRuleAction
{
    KeepRunning,
    Pause,
    Stop,
    LoadScene,
    LoadPlaylist,
    LoadProfile
}

public sealed class ScenePlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "播放列表";
    public List<string> SceneIds { get; set; } = [];
    public int IntervalMinutes { get; set; } = 10;
    public bool Shuffle { get; set; }
    public bool ChangeWhilePaused { get; set; }
}

public sealed class MonitorSceneAssignment
{
    public string MonitorKey { get; set; } = "primary";
    public string? SceneId { get; set; }
    public string? PlaylistId { get; set; }
}

public sealed class DesktopProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "桌面配置";
    public List<MonitorSceneAssignment> Monitors { get; set; } = [];
}

public sealed class ApplicationPlaybackRule
{
    public string ExeName { get; set; } = string.Empty;
    public ApplicationRuleCondition Condition { get; set; } = ApplicationRuleCondition.Running;
    public ApplicationRuleAction Action { get; set; } = ApplicationRuleAction.Pause;
    public string? TargetId { get; set; }
}

public sealed class DesktopPlaybackSettings
{
    public PlaybackBehavior FullscreenBehavior { get; set; } = PlaybackBehavior.Pause;
    public PlaybackBehavior MaximizedBehavior { get; set; } = PlaybackBehavior.KeepRunning;
    public int GlobalFpsLimit { get; set; } = 60;
    public double GlobalVolume { get; set; }
    public string? ActivePlaylistId { get; set; }
    public string? ActiveProfileId { get; set; }
    public List<ScenePlaylist> Playlists { get; set; } = [];
    public List<DesktopProfile> Profiles { get; set; } = [];
    public List<ApplicationPlaybackRule> ApplicationRules { get; set; } = [];
}

public sealed class DesktopPlaybackSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public DesktopPlaybackSettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuringDesk");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "playback-settings.json");
    }

    public DesktopPlaybackSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new DesktopPlaybackSettings();
            var settings = JsonSerializer.Deserialize<DesktopPlaybackSettings>(File.ReadAllText(_path), JsonOptions)
                ?? new DesktopPlaybackSettings();
            settings.GlobalFpsLimit = Math.Clamp(settings.GlobalFpsLimit, 1, 240);
            settings.GlobalVolume = Math.Clamp(settings.GlobalVolume, 0, 1);
            settings.Playlists ??= [];
            settings.Profiles ??= [];
            settings.ApplicationRules ??= [];
            return settings;
        }
        catch
        {
            return new DesktopPlaybackSettings();
        }
    }

    public void Save(DesktopPlaybackSettings settings)
    {
        settings.GlobalFpsLimit = Math.Clamp(settings.GlobalFpsLimit, 1, 240);
        settings.GlobalVolume = Math.Clamp(settings.GlobalVolume, 0, 1);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, true);
    }
}
