using System.Text.Json.Serialization;

namespace TuringDesk.Desktop.Services.SceneEngine;

public enum SceneKind
{
    Scene,
    Video,
    Web
}

public enum SceneFit
{
    Cover,
    Contain,
    Stretch
}

public enum ScenePropertyKind
{
    Color,
    Slider,
    Bool,
    Combo,
    Text,
    File,
    Shortcut
}

public sealed class SceneManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Author { get; set; }
    public SceneKind Kind { get; set; } = SceneKind.Scene;
    public string? Entry { get; set; }
    public string? Preview { get; set; }
    public SceneFit Fit { get; set; } = SceneFit.Cover;
    public int PreferredFps { get; set; } = 60;
    public bool Interactive { get; set; }
    public bool AudioReactive { get; set; }
    public bool MediaIntegration { get; set; }
    public bool Muted { get; set; } = true;
    public List<string> Tags { get; set; } = [];
    public List<ScenePropertyDefinition> Properties { get; set; } = [];
    public Dictionary<string, object?> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string PackageRoot { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public string? ResolveEntryPath()
    {
        if (string.IsNullOrWhiteSpace(Entry)) return null;
        if (Path.IsPathRooted(Entry)) return Entry;
        if (string.IsNullOrWhiteSpace(PackageRoot)) return null;
        return Path.GetFullPath(Path.Combine(PackageRoot, Entry));
    }
}

public sealed class ScenePropertyDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Group { get; set; }
    public ScenePropertyKind Kind { get; set; }
    public object? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public List<ScenePropertyOption> Options { get; set; } = [];
    public string? FileFilter { get; set; }
}

public sealed record ScenePropertyOption(string Label, string Value);

public sealed class SceneInstanceSettings
{
    public string SceneId { get; set; } = "builtin:aurora";
    public Dictionary<string, object?> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Volume { get; set; }
    public int FpsLimit { get; set; } = 60;
    public bool Muted { get; set; } = true;
}
