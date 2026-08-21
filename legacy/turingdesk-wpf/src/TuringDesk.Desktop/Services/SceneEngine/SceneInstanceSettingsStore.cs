using System.Text.Json;

namespace TuringDesk.Desktop.Services.SceneEngine;

public sealed class SceneInstanceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public static event Action<string>? SceneSettingsChanged;

    public SceneInstanceSettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuringDesk");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "scene-instance-settings.json");
    }

    public SceneInstanceSettings Load(SceneManifest scene)
    {
        var all = LoadAll();
        if (!all.TryGetValue(scene.Id, out var settings))
        {
            settings = new SceneInstanceSettings
            {
                SceneId = scene.Id,
                FpsLimit = scene.PreferredFps,
                Muted = scene.Muted,
                Volume = 0
            };
        }

        settings.SceneId = scene.Id;
        settings.Properties ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in scene.Properties)
        {
            if (string.IsNullOrWhiteSpace(definition.Key) || settings.Properties.ContainsKey(definition.Key)) continue;
            settings.Properties[definition.Key] = definition.Default ?? scene.Defaults.GetValueOrDefault(definition.Key);
        }
        foreach (var pair in scene.Defaults)
        {
            if (!settings.Properties.ContainsKey(pair.Key)) settings.Properties[pair.Key] = pair.Value;
        }
        settings.FpsLimit = Math.Clamp(settings.FpsLimit, 1, 240);
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        return settings;
    }

    public void Save(SceneInstanceSettings settings)
    {
        settings.FpsLimit = Math.Clamp(settings.FpsLimit, 1, 240);
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        settings.Properties ??= new(StringComparer.OrdinalIgnoreCase);
        var all = LoadAll();
        all[settings.SceneId] = settings;
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonOptions));
        File.Move(temp, _path, true);
        SceneSettingsChanged?.Invoke(settings.SceneId);
    }

    private Dictionary<string, SceneInstanceSettings> LoadAll()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var result = JsonSerializer.Deserialize<Dictionary<string, SceneInstanceSettings>>(File.ReadAllText(_path), JsonOptions)
                ?? new Dictionary<string, SceneInstanceSettings>();
            return new Dictionary<string, SceneInstanceSettings>(result, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
