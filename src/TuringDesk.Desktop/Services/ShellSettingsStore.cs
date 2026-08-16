using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed record PinnedShellApp(string Name, string Target, string? IconTarget, string Glyph = "◆");

public sealed class ShellSettings
{
    public List<PinnedShellApp> PinnedApps { get; set; } = new();
}

public sealed class ShellSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public static event Action? SettingsChanged;

    public ShellSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuringDesk");
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
            // A damaged preferences file should never prevent the shell from starting.
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
            // Preferences are best-effort; shell usability must win over persistence.
        }
    }

    private static ShellSettings Normalize(ShellSettings settings) => new()
    {
        PinnedApps = settings.PinnedApps
            .Where(app => !string.IsNullOrWhiteSpace(app.Target))
            .DistinctBy(app => app.Target, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList()
    };

    private static ShellSettings CreateDefaults() => new()
    {
        PinnedApps =
        [
            new("Chrome", "chrome", null, "◉"),
            new("VS Code", "code", null, "⌘"),
            new("Terminal", "terminal", null, ">_")
        ]
    };
}
