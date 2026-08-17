using System.IO.Compression;
using System.Text.Json;

namespace TuringDesk.Desktop.Services.SceneEngine;

public sealed class SceneCatalogService
{
    private const string ManifestFileName = "scene.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _sceneRoot;

    public SceneCatalogService()
    {
        _sceneRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuringDesk",
            "Scenes");
        Directory.CreateDirectory(_sceneRoot);
    }

    public IReadOnlyList<SceneManifest> LoadAll()
    {
        var scenes = new List<SceneManifest>();
        scenes.AddRange(BuiltInScenes.Create());

        foreach (var directory in Directory.EnumerateDirectories(_sceneRoot))
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<SceneManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Title)) continue;
                manifest.PackageRoot = directory;
                manifest.IsBuiltIn = false;
                Normalize(manifest);
                scenes.Add(manifest);
            }
            catch
            {
                // One broken user package must never stop the desktop engine.
            }
        }

        return scenes
            .GroupBy(scene => scene.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(scene => scene.IsBuiltIn)
            .ThenBy(scene => scene.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public SceneManifest? Find(string? id) =>
        LoadAll().FirstOrDefault(scene => string.Equals(scene.Id, id, StringComparison.OrdinalIgnoreCase));

    public SceneManifest Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Scene package path is required.", nameof(sourcePath));
        sourcePath = Path.GetFullPath(sourcePath);

        string stagingRoot;
        var deleteStaging = false;
        if (Directory.Exists(sourcePath))
        {
            stagingRoot = sourcePath;
        }
        else if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".tdscene", StringComparison.OrdinalIgnoreCase))
        {
            stagingRoot = Path.Combine(Path.GetTempPath(), "TuringDesk", "scene-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);
            ZipFile.ExtractToDirectory(sourcePath, stagingRoot);
            deleteStaging = true;
        }
        else
        {
            throw new FileNotFoundException("Choose a TuringDesk .tdscene package or a scene project folder.", sourcePath);
        }

        try
        {
            var manifestPath = Path.Combine(stagingRoot, ManifestFileName);
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Scene package is missing scene.json.");
            var manifest = JsonSerializer.Deserialize<SceneManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("scene.json could not be parsed.");
            Normalize(manifest);
            Validate(manifest, stagingRoot);

            var safeId = string.Concat(manifest.Id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
            if (string.IsNullOrWhiteSpace(safeId)) safeId = Guid.NewGuid().ToString("N");
            var destination = Path.Combine(_sceneRoot, safeId);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            CopyDirectory(stagingRoot, destination);

            manifest.PackageRoot = destination;
            manifest.IsBuiltIn = false;
            return manifest;
        }
        finally
        {
            if (deleteStaging)
            {
                try { Directory.Delete(stagingRoot, recursive: true); } catch { }
            }
        }
    }

    public string Export(string sceneId, string destinationFile)
    {
        var scene = Find(sceneId) ?? throw new InvalidOperationException($"Scene not found: {sceneId}");
        if (scene.IsBuiltIn) throw new InvalidOperationException("Built-in scenes are part of TuringDesk and are not exported as project packages.");
        if (string.IsNullOrWhiteSpace(scene.PackageRoot) || !Directory.Exists(scene.PackageRoot)) throw new DirectoryNotFoundException(scene.PackageRoot);

        destinationFile = Path.GetFullPath(destinationFile);
        if (!destinationFile.EndsWith(".tdscene", StringComparison.OrdinalIgnoreCase)) destinationFile += ".tdscene";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        if (File.Exists(destinationFile)) File.Delete(destinationFile);
        ZipFile.CreateFromDirectory(scene.PackageRoot, destinationFile, CompressionLevel.Fastest, includeBaseDirectory: false);
        return destinationFile;
    }

    private static void Validate(SceneManifest manifest, string root)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported scene schema version: {manifest.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException("Scene id is required.");
        if (string.IsNullOrWhiteSpace(manifest.Title)) throw new InvalidDataException("Scene title is required.");

        if (manifest.Kind is SceneKind.Video or SceneKind.Web)
        {
            if (string.IsNullOrWhiteSpace(manifest.Entry)) throw new InvalidDataException($"{manifest.Kind} scenes require an entry file.");
            var entry = Path.GetFullPath(Path.Combine(root, manifest.Entry));
            if (!entry.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
                throw new InvalidDataException("Scene entry file is missing or outside the package directory.");
        }
    }

    private static void Normalize(SceneManifest manifest)
    {
        manifest.Id = manifest.Id.Trim();
        manifest.Title = manifest.Title.Trim();
        manifest.PreferredFps = Math.Clamp(manifest.PreferredFps, 1, 240);
        manifest.Tags ??= [];
        manifest.Properties ??= [];
        manifest.Defaults ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var property in manifest.Properties)
        {
            property.Key = property.Key?.Trim() ?? string.Empty;
            property.Label = string.IsNullOrWhiteSpace(property.Label) ? property.Key : property.Label.Trim();
            property.Options ??= [];
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}

internal static class BuiltInScenes
{
    public static IEnumerable<SceneManifest> Create()
    {
        yield return BuiltIn("builtin:aurora", "Aurora Flow", "柔和流动的极光光带，默认桌面。", "aurora", audio: false);
        yield return BuiltIn("builtin:neon", "Neon Grid", "赛博霓虹与网格流光。", "neon", audio: true);
        yield return BuiltIn("builtin:orbit", "Orbital Calm", "深空轨道与缓慢粒子运动。", "orbit", audio: false);
    }

    private static SceneManifest BuiltIn(string id, string title, string description, string preset, bool audio) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Author = "TuringDesk",
        Kind = SceneKind.Scene,
        IsBuiltIn = true,
        PreferredFps = 60,
        Interactive = true,
        AudioReactive = audio,
        Tags = ["builtin", preset],
        Properties =
        [
            new() { Key = "intensity", Label = "动态强度", Kind = ScenePropertyKind.Slider, Default = 0.85, Min = 0.2, Max = 1.0, Step = 0.05 },
            new() { Key = "motion", Label = "动态效果", Kind = ScenePropertyKind.Bool, Default = true },
            new() { Key = "accent", Label = "强调色", Kind = ScenePropertyKind.Color, Default = "#8796FF" }
        ]
    };
}
