using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuringDesk.Desktop.Services.SceneEngine;

public sealed class SceneCatalogService
{
    private const string ManifestFileName = "scene.json";
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mov", ".m4v", ".avi" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
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
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Scene source path is required.", nameof(sourcePath));
        sourcePath = Path.GetFullPath(sourcePath);

        if (File.Exists(sourcePath))
        {
            var extension = Path.GetExtension(sourcePath);
            if (VideoExtensions.Contains(extension)) return ImportSimpleFile(sourcePath, SceneKind.Video);
            if (ImageExtensions.Contains(extension)) return ImportSimpleFile(sourcePath, SceneKind.Scene);
            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                return ImportWebProject(sourcePath);
        }

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
            throw new FileNotFoundException("请选择图片、视频、HTML、.tdscene 或场景项目文件夹。", sourcePath);
        }

        try
        {
            var manifestPath = Path.Combine(stagingRoot, ManifestFileName);
            if (!File.Exists(manifestPath)) throw new InvalidDataException("高级场景项目需要 scene.json。简单图片/视频/HTML 可直接选择文件导入。");
            var manifest = JsonSerializer.Deserialize<SceneManifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("scene.json could not be parsed.");
            Normalize(manifest);
            Validate(manifest, stagingRoot);
            return InstallFromStaging(manifest, stagingRoot);
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

    public void Save(SceneManifest manifest)
    {
        if (manifest.IsBuiltIn) throw new InvalidOperationException("Built-in scenes cannot be overwritten. Duplicate the scene before editing it.");
        if (string.IsNullOrWhiteSpace(manifest.PackageRoot))
        {
            manifest.PackageRoot = Path.Combine(_sceneRoot, SafeId(manifest.Id));
        }
        Normalize(manifest);
        Directory.CreateDirectory(manifest.PackageRoot);
        Validate(manifest, manifest.PackageRoot);
        WriteManifest(manifest.PackageRoot, manifest);
    }

    public SceneManifest CreateProject(string title)
    {
        var id = "user:" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(_sceneRoot, SafeId(id));
        Directory.CreateDirectory(root);
        var manifest = new SceneManifest
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "新场景" : title.Trim(),
            Description = "TuringDesk Scene Editor project",
            Author = Environment.UserName,
            Kind = SceneKind.Scene,
            PreferredFps = 60,
            Interactive = true,
            PackageRoot = root,
            IsBuiltIn = false
        };
        WriteManifest(root, manifest);
        return manifest;
    }

    public SceneManifest DuplicateForEditing(SceneManifest source)
    {
        var id = "user:" + Guid.NewGuid().ToString("N");
        var destination = Path.Combine(_sceneRoot, SafeId(id));
        Directory.CreateDirectory(destination);
        if (!source.IsBuiltIn && Directory.Exists(source.PackageRoot)) CopyDirectory(source.PackageRoot, destination);

        var json = JsonSerializer.Serialize(source, JsonOptions);
        var copy = JsonSerializer.Deserialize<SceneManifest>(json, JsonOptions) ?? new SceneManifest();
        copy.Id = id;
        copy.Title = source.Title + " · 副本";
        copy.Author = Environment.UserName;
        copy.PackageRoot = destination;
        copy.IsBuiltIn = false;
        WriteManifest(destination, copy);
        return copy;
    }

    private SceneManifest ImportSimpleFile(string sourcePath, SceneKind kind)
    {
        var id = "user:" + Guid.NewGuid().ToString("N");
        var title = Path.GetFileNameWithoutExtension(sourcePath);
        var package = Path.Combine(_sceneRoot, SafeId(id));
        Directory.CreateDirectory(package);
        var assetName = "content" + Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, Path.Combine(package, assetName), overwrite: true);

        var manifest = new SceneManifest
        {
            Id = id,
            Title = title,
            Author = Environment.UserName,
            Kind = kind,
            Entry = assetName,
            PreferredFps = kind == SceneKind.Video ? 60 : 30,
            Muted = true,
            Fit = SceneFit.Cover,
            Tags = kind == SceneKind.Video ? ["video", "imported"] : ["scene", "image", "imported"],
            PackageRoot = package,
            Layers = kind == SceneKind.Scene
                ? [new SceneLayerDefinition { Name = "背景", Kind = SceneLayerKind.Image, Source = assetName, Width = 1, Height = 1 }]
                : []
        };
        WriteManifest(package, manifest);
        return manifest;
    }

    private SceneManifest ImportWebProject(string entryHtml)
    {
        var id = "user:" + Guid.NewGuid().ToString("N");
        var title = Path.GetFileNameWithoutExtension(entryHtml);
        var sourceRoot = Path.GetDirectoryName(entryHtml) ?? throw new InvalidDataException("HTML source folder is unavailable.");
        ValidateWebProjectSize(sourceRoot);

        var package = Path.Combine(_sceneRoot, SafeId(id));
        var webRoot = Path.Combine(package, "web");
        CopyDirectory(sourceRoot, webRoot);
        var relativeEntry = Path.Combine("web", Path.GetFileName(entryHtml));

        var manifest = new SceneManifest
        {
            Id = id,
            Title = title,
            Author = Environment.UserName,
            Kind = SceneKind.Web,
            Entry = relativeEntry,
            PreferredFps = 60,
            Interactive = true,
            Muted = true,
            Tags = ["web", "imported"],
            PackageRoot = package
        };
        WriteManifest(package, manifest);
        return manifest;
    }

    private SceneManifest InstallFromStaging(SceneManifest manifest, string stagingRoot)
    {
        var destination = Path.Combine(_sceneRoot, SafeId(manifest.Id));
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        CopyDirectory(stagingRoot, destination);
        manifest.PackageRoot = destination;
        manifest.IsBuiltIn = false;
        WriteManifest(destination, manifest);
        return manifest;
    }

    private static void WriteManifest(string root, SceneManifest manifest)
    {
        Directory.CreateDirectory(root);
        // PackageRoot and IsBuiltIn are [JsonIgnore], so serializing the manifest
        // directly is both safe and future-proof: layer/effect/particle/timeline/
        // script fields cannot silently disappear when the schema grows.
        File.WriteAllText(Path.Combine(root, ManifestFileName), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static void Validate(SceneManifest manifest, string root)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported scene schema version: {manifest.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException("Scene id is required.");
        if (string.IsNullOrWhiteSpace(manifest.Title)) throw new InvalidDataException("Scene title is required.");

        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            ValidateAssetPath(root, manifest.Entry, "Scene entry");
        }
        else if (manifest.Kind is SceneKind.Video or SceneKind.Web)
        {
            throw new InvalidDataException($"{manifest.Kind} scenes require an entry file.");
        }

        foreach (var layer in manifest.Layers.Where(layer => !string.IsNullOrWhiteSpace(layer.Source)))
        {
            ValidateAssetPath(root, layer.Source!, $"Layer '{layer.Name}' source");
        }
        if (!string.IsNullOrWhiteSpace(manifest.Script)) ValidateAssetPath(root, manifest.Script!, "Scene script");
    }

    private static void ValidateAssetPath(string root, string relativePath, string label)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidDataException($"{label} is missing or outside the package directory: {relativePath}");
    }

    private static void ValidateWebProjectSize(string root)
    {
        const long maxBytes = 512L * 1024 * 1024;
        const int maxFiles = 5000;
        long bytes = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            count++;
            if (count > maxFiles) throw new InvalidDataException("Web 项目文件过多，请先整理到独立项目文件夹后再导入。");
            try { bytes += new FileInfo(path).Length; } catch { }
            if (bytes > maxBytes) throw new InvalidDataException("Web 项目超过 512 MB，请精简后再导入。");
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
        manifest.Layers ??= [];
        manifest.Timeline ??= [];
        foreach (var property in manifest.Properties)
        {
            property.Key = property.Key?.Trim() ?? string.Empty;
            property.Label = string.IsNullOrWhiteSpace(property.Label) ? property.Key : property.Label.Trim();
            property.Options ??= [];
        }
        foreach (var layer in manifest.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id)) layer.Id = Guid.NewGuid().ToString("N");
            layer.Name = string.IsNullOrWhiteSpace(layer.Name) ? layer.Kind.ToString() : layer.Name.Trim();
            layer.Opacity = Math.Clamp(layer.Opacity, 0, 1);
            layer.Scale = Math.Clamp(layer.Scale, 0.01, 100);
            layer.Effects ??= [];
        }
    }

    private static string SafeId(string id)
    {
        var safe = string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
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
