using System.Text.Json.Serialization;

namespace TuringDesk.Desktop.SceneEngine;

public enum SceneProjectType
{
    Image,
    Video,
    Web,
    Scene
}

public enum ScenePlaybackState
{
    Running,
    Muted,
    Paused,
    Stopped
}

public enum ScenePropertyType
{
    Color,
    Slider,
    Toggle,
    Combo,
    Text,
    Media,
    Shortcut,
    Group
}

public sealed class ScenePackage
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Scene";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public SceneProjectType Type { get; set; } = SceneProjectType.Scene;
    public string Entry { get; set; } = string.Empty;
    public string? Preview { get; set; }
    public List<string> Tags { get; set; } = [];
    public SceneCapabilities Capabilities { get; set; } = new();
    public ScenePerformanceDefaults Performance { get; set; } = new();
    public List<SceneUserProperty> Properties { get; set; } = [];
    public List<SceneLayerDefinition> Layers { get; set; } = [];
    public SceneAudioDefinition Audio { get; set; } = new();
    public SceneInteractionDefinition Interaction { get; set; } = new();
}

public sealed class SceneCapabilities
{
    public bool UsesAudioInput { get; set; }
    public bool UsesSceneAudio { get; set; }
    public bool UsesMouse { get; set; }
    public bool UsesWeb { get; set; }
    public bool UsesVideo { get; set; }
    public bool UsesShaders { get; set; }
    public bool UsesScripts { get; set; }
    public bool UsesParticles { get; set; }
    public bool UsesTimeline { get; set; }
}

public sealed class ScenePerformanceDefaults
{
    public int TargetFps { get; set; } = 30;
    public int? MaxFps { get; set; } = 60;
    public string Quality { get; set; } = "high";
    public bool PauseOnBatterySaver { get; set; } = true;
    public ScenePlaybackState FullscreenBehavior { get; set; } = ScenePlaybackState.Paused;
}

public sealed class SceneUserProperty
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScenePropertyType Type { get; set; }
    public object? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public List<ScenePropertyOption> Options { get; set; } = [];
    public string? Group { get; set; }
    public string? Help { get; set; }
}

public sealed class ScenePropertyOption
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class SceneLayerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public string Kind { get; set; } = "image";
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public int Order { get; set; }
    public SceneTransform Transform { get; set; } = new();
    public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SceneEffectDefinition> Effects { get; set; } = [];
    public List<SceneAnimationTrack> Timeline { get; set; } = [];
}

public sealed class SceneTransform
{
    public double X { get; set; }
    public double Y { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double Rotation { get; set; }
    public double ParallaxDepth { get; set; }
}

public sealed class SceneEffectDefinition
{
    public string Type { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SceneAnimationTrack
{
    public string Property { get; set; } = string.Empty;
    public string Interpolation { get; set; } = "linear";
    public bool Loop { get; set; }
    public List<SceneKeyframe> Keyframes { get; set; } = [];
}

public sealed class SceneKeyframe
{
    public double Time { get; set; }
    public object? Value { get; set; }
    public string? Easing { get; set; }
}

public sealed class SceneAudioDefinition
{
    public bool Enabled { get; set; }
    public double Volume { get; set; } = 1;
    public bool ReactToSystemAudio { get; set; }
    public string? Source { get; set; }
}

public sealed class SceneInteractionDefinition
{
    public bool MouseParallax { get; set; }
    public bool PointerInput { get; set; }
    public double ParallaxStrength { get; set; } = 0.15;
    public string? Script { get; set; }
}

public sealed class ScenePlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Playlist";
    public bool Shuffle { get; set; }
    public int RotationMinutes { get; set; } = 30;
    public List<string> SceneIds { get; set; } = [];
}

public sealed class MonitorSceneAssignment
{
    public string MonitorId { get; set; } = string.Empty;
    public string? SceneId { get; set; }
    public string? PlaylistId { get; set; }
}

public sealed class DesktopSceneProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profile";
    public string LayoutMode { get; set; } = "independent";
    public List<MonitorSceneAssignment> Monitors { get; set; } = [];
}

public sealed class SceneApplicationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExecutableName { get; set; } = string.Empty;
    public string Condition { get; set; } = "running";
    public ScenePlaybackState? Playback { get; set; }
    public string? SceneId { get; set; }
    public string? PlaylistId { get; set; }
    public string? ProfileId { get; set; }
    public string? AiProfileId { get; set; }
}
