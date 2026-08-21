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
}

public sealed class ShellSettings
{
    public List<PinnedShellApp> PinnedApps { get; set; } = new();
    public ShellAppearanceSettings Appearance { get; set; } = new();
}

public sealed class ShellSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object TraceGate = new();
    private static string? LastObservedSignature;
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
                if (settings is not null)
                {
                    var normalized = Normalize(settings);
                    TraceObserved(normalized, "read");
                    return normalized;
                }
            }
        }
        catch (Exception error)
        {
            SceneEngineTrace.Error("settings.shell.read", $"failed path={_path}", error);
        }

        var defaults = CreateDefaults();
        TraceObserved(defaults, File.Exists(_path) ? "fallback-after-read-failure" : "defaults-no-file");
        return defaults;
    }

    public void Save(ShellSettings settings)
    {
        try
        {
            var normalized = Normalize(settings);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, _path, true);
            SceneEngineTrace.Info(
                "settings.shell.save",
                $"scene={normalized.Appearance.SceneId} wallpaperMode={normalized.Appearance.WallpaperMode} motion={normalized.Appearance.SceneMotionEnabled} intensity={normalized.Appearance.SceneIntensity:0.###} path={_path}");
            TraceObserved(normalized, "save");
            SettingsChanged?.Invoke();
        }
        catch (Exception error)
        {
            SceneEngineTrace.Error("settings.shell.save", $"failed path={_path} requestedScene={settings.Appearance?.SceneId ?? "<null>"}", error);
        }
    }

    public void ResetAppearance()
    {
        var settings = Load();
        settings.Appearance = CreateDefaultAppearance();
        Save(settings);
    }

    private static void TraceObserved(ShellSettings settings, string source)
    {
        var appearance = settings.Appearance;
        var signature = $"{appearance.SceneId}|{appearance.WallpaperMode}|{appearance.WallpaperPath}|{appearance.SceneMotionEnabled}|{appearance.SceneIntensity:0.###}";
        lock (TraceGate)
        {
            if (string.Equals(LastObservedSignature, signature, StringComparison.Ordinal)) return;
            LastObservedSignature = signature;
        }

        SceneEngineTrace.Info(
            "settings.shell.observe",
            $"source={source} scene={appearance.SceneId} wallpaperMode={appearance.WallpaperMode} wallpaperPath={appearance.WallpaperPath ?? "<null>"} motion={appearance.SceneMotionEnabled} intensity={appearance.SceneIntensity:0.###}");
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
