using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed record PinnedShellApp(string Name, string Target, string? IconTarget, string Glyph = "App");

public sealed class ShellAppearanceSettings
{
    public string WallpaperMode { get; set; } = "system";
    public string? WallpaperPath { get; set; }
    public string WallpaperFit { get; set; } = "cover";
    public string AccentHex { get; set; } = "#8796FF";
    public double TaskbarOpacity { get; set; } = 0.96;

    // Desktop Engine state. SceneId can reference a built-in scene or an imported
    // scene package. Renderer type is resolved from the scene manifest.
    public string SceneId { get; set; } = "builtin:aurora";
    public bool SceneMotionEnabled { get; set; } = true;
    public double SceneIntensity { get; set; } = 0.86;
    public bool PauseSceneOnFullscreen { get; set; } = true;

    // Lightweight Agent surfaces stay independent from the optional Harness WebUI.
    public bool AgentOrbEnabled { get; set; } = true;
    public bool AgentCardsEnabled { get; set; } = true;
    public double AgentCardOpacity { get; set; } = 0.96;
    public int AgentCardAutoHideSeconds { get; set; } = 12;
    public string AgentCardSide { get; set; } = "right";
}

public sealed class ShellSettings
{
    public List<PinnedShellApp> PinnedApps { get; set; } = new();
    public ShellAppearanceSettings Appearance { get; set; } = new();
}

public sealed class ShellSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public static event Action? SettingsChanged;

    public ShellSettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuringDesk");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "shell-settings.json");
    }

    public ShellSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(_path), JsonOptions);
                if (settings is not null) return Normalize(settings);
            }
        }
        catch
        {
            // A damaged preferences file should never prevent the desktop from starting.
        }

        return CreateDefaults();
    }

    public void Save(ShellSettings settings)
    {
        try
        {
            var normalized = Normalize(settings);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, _path, true);
            SettingsChanged?.Invoke();
        }
        catch
        {
            // Preferences are best-effort; Windows desktop usability must win.
        }
    }

    public void ResetAppearance()
    {
        var settings = Load();
        settings.Appearance = CreateDefaultAppearance();
        Save(settings);
    }

    private static ShellSettings Normalize(ShellSettings settings)
    {
        var appearance = settings.Appearance ?? new ShellAppearanceSettings();
        appearance.WallpaperMode = appearance.WallpaperMode is "system" or "custom" or "solid" ? appearance.WallpaperMode : "system";
        appearance.WallpaperPath = string.IsNullOrWhiteSpace(appearance.WallpaperPath) ? null : appearance.WallpaperPath.Trim();
        appearance.WallpaperFit = appearance.WallpaperFit is "cover" or "contain" or "stretch" ? appearance.WallpaperFit : "cover";
        appearance.AccentHex = NormalizeHex(appearance.AccentHex, "#8796FF");
        appearance.TaskbarOpacity = Math.Clamp(appearance.TaskbarOpacity, 0.60, 1.0);
        appearance.SceneId = NormalizeSceneId(appearance.SceneId);
        appearance.SceneIntensity = Math.Clamp(appearance.SceneIntensity, 0.20, 1.0);
        appearance.AgentCardOpacity = Math.Clamp(appearance.AgentCardOpacity, 0.70, 1.0);
        appearance.AgentCardAutoHideSeconds = Math.Clamp(appearance.AgentCardAutoHideSeconds, 0, 60);
        appearance.AgentCardSide = appearance.AgentCardSide is "left" or "right" ? appearance.AgentCardSide : "right";

        return new ShellSettings
        {
            PinnedApps = settings.PinnedApps
                .Where(app => !string.IsNullOrWhiteSpace(app.Target))
                .DistinctBy(app => app.Target, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList(),
            Appearance = appearance
        };
    }

    private static string NormalizeSceneId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "builtin:aurora";
        var id = value.Trim();
        // Migrate the three v0.13 preview ids without breaking existing settings.
        return id.ToLowerInvariant() switch
        {
            "aurora" => "builtin:aurora",
            "neon" => "builtin:neon",
            "orbit" => "builtin:orbit",
            _ => id
        };
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = $"#{text}";
        if (text.Length != 7) return fallback;
        return text.Skip(1).All(Uri.IsHexDigit) ? text.ToUpperInvariant() : fallback;
    }

    private static ShellAppearanceSettings CreateDefaultAppearance() => new();

    private static ShellSettings CreateDefaults() => new()
    {
        PinnedApps =
        [
            new("Chrome", "chrome", null, "Browser"),
            new("VS Code", "code", null, "Code"),
            new("Terminal", "terminal", null, "Terminal")
        ],
        Appearance = CreateDefaultAppearance()
    };
}
