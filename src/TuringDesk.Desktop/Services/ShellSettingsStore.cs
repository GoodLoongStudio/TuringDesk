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
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // A damaged preferences file should never prevent the shell from starting.
        }

        return new ShellSettings
        {
            PinnedApps =
            [
                new("Chrome", "chrome", null, "◉"),
                new("VS Code", "code", null, "⌘"),
                new("Terminal", "terminal", null, ">_")
            ]
        };
    }

    public void Save(ShellSettings settings)
    {
        try
        {
            var normalized = new ShellSettings
            {
                PinnedApps = settings.PinnedApps
                    .Where(app => !string.IsNullOrWhiteSpace(app.Target))
                    .DistinctBy(app => app.Target, StringComparer.OrdinalIgnoreCase)
                    .Take(24)
                    .ToList()
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(normalized, JsonOptions));
        }
        catch
        {
            // Preferences are best-effort; shell usability must win over persistence.
        }
    }
}
