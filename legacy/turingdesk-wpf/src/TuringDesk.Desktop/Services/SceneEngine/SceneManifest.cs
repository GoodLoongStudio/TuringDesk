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

public enum SceneLayerKind
{
    Image,
    Text,
    Shape,
    Particle,
    Audio,
    Model3D
}

public enum SceneBlendMode
{
    Normal,
    Add,
    Multiply,
    Screen
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
    public string? Script { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<ScenePropertyDefinition> Properties { get; set; } = [];
    public Dictionary<string, object?> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SceneLayerDefinition> Layers { get; set; } = [];
    public List<SceneTimelineTrack> Timeline { get; set; } = [];

    /// <summary>
    /// Permission declarations for web/script scenes. Spec §11.3: web/script
    /// scenes must declare network and file-access policy.
    /// </summary>
    public ScenePermissions Permissions { get; set; } = new();

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

public sealed class SceneLayerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Layer";
    public SceneLayerKind Kind { get; set; } = SceneLayerKind.Image;
    public string? Source { get; set; }
    public string? Text { get; set; }
    public bool Visible { get; set; } = true;
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double Width { get; set; } = 1.0;
    public double Height { get; set; } = 1.0;
    public double Scale { get; set; } = 1.0;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1.0;
    public double Depth { get; set; }
    public SceneBlendMode BlendMode { get; set; } = SceneBlendMode.Normal;
    public bool MouseParallax { get; set; }
    public double ParallaxStrength { get; set; } = 0.02;
    public bool AudioReactive { get; set; }
    public double AudioStrength { get; set; } = 1.0;
    public SceneParticleDefinition? Particle { get; set; }
    public List<SceneEffectDefinition> Effects { get; set; } = [];
}

public sealed class SceneEffectDefinition
{
    public string Type { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Numbers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SceneParticleDefinition
{
    public int MaxParticles { get; set; } = 128;
    public double SpawnRate { get; set; } = 12;
    public double MinSize { get; set; } = 1.5;
    public double MaxSize { get; set; } = 4.0;
    public double MinSpeed { get; set; } = 12;
    public double MaxSpeed { get; set; } = 40;
    public double LifetimeSeconds { get; set; } = 12;
    public string Color { get; set; } = "#DDE6FF";
    public bool FollowPointer { get; set; }
    public double PointerForce { get; set; }
    public bool AudioReactive { get; set; }
}

public sealed class SceneTimelineTrack
{
    public string LayerId { get; set; } = string.Empty;
    public string Property { get; set; } = string.Empty;
    public bool Loop { get; set; } = true;
    public List<SceneKeyframe> Keyframes { get; set; } = [];
}

public sealed class SceneKeyframe
{
    public double TimeSeconds { get; set; }
    public double Value { get; set; }
    public string Easing { get; set; } = "linear";
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

/// <summary>
/// Declares what a web/script scene is allowed to access. Default-deny.
/// Spec §11.3: "对 Web/脚本场景声明权限和网络策略".
/// </summary>
public sealed class ScenePermissions
{
    /// <summary>Allow the scene to make outbound network requests.</summary>
    public bool AllowNetwork { get; set; } = false;

    /// <summary>Allow the scene to read files from its own package directory.</summary>
    public bool AllowLocalFiles { get; set; } = true;

    /// <summary>Allow the scene to execute user-provided scripts.</summary>
    public bool AllowScripts { get; set; } = false;

    /// <summary>Maximum allowed memory in MB, or 0 for unlimited.</summary>
    public int MaxMemoryMb { get; set; } = 0;
}
