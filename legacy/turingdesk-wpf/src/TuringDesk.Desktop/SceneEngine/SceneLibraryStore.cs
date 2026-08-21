using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TuringDesk.Desktop.SceneEngine;

public sealed class SceneLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly string _libraryRoot;
    private readonly string _statePath;

    public SceneLibraryStore()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuringDesk", "Scenes");
        _libraryRoot = Path.Combine(_root, "Library");
        _statePath = Path.Combine(_root, "library-state.json");
        Directory.CreateDirectory(_libraryRoot);
    }

    public string LibraryRoot => _libraryRoot;

    public IReadOnlyList<InstalledScene> LoadInstalledScenes()
    {
        var result = new List<InstalledScene>();
        foreach (var manifest in Directory.EnumerateFiles(_libraryRoot, "scene.json", SearchOption.AllDirectories))
        {
            try
            {
                var package = JsonSerializer.Deserialize<ScenePackage>(File.ReadAllText(manifest), JsonOptions);
                if (package is null || string.IsNullOrWhiteSpace(package.Id)) continue;
                var root = Path.GetDirectoryName(manifest) ?? _libraryRoot;
                result.Add(new InstalledScene(package, root, manifest, File.GetCreationTimeUtc(manifest)));
            }
            catch
            {
                // One broken user package must not make the whole library unusable.
            }
        }

        return result
            .OrderBy(scene => scene.Package.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<InstalledScene> ImportFolderAsync(string sourceFolder, CancellationToken cancellationToken = default)
    {
        var sourceManifest = Path.Combine(sourceFolder, "scene.json");
        if (!File.Exists(sourceManifest)) throw new InvalidDataException("The selected folder does not contain scene.json.");

        var package = JsonSerializer.Deserialize<ScenePackage>(await File.ReadAllTextAsync(sourceManifest, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("scene.json is invalid.");
        ValidatePackage(package);

        var destination = Path.Combine(_libraryRoot, SanitizeFolderName(package.Id));
        var temporary = destination + ".importing";
        if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        Directory.CreateDirectory(temporary);
        CopyDirectory(sourceFolder, temporary, cancellationToken);

        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.Move(temporary, destination);

        var manifest = Path.Combine(destination, "scene.json");
        return new InstalledScene(package, destination, manifest, DateTimeOffset.UtcNow);
    }

    public SceneLibraryState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                return JsonSerializer.Deserialize<SceneLibraryState>(File.ReadAllText(_statePath), JsonOptions) ?? new SceneLibraryState();
            }
        }
        catch { }
        return new SceneLibraryState();
    }

    public void SaveState(SceneLibraryState state)
    {
        Directory.CreateDirectory(_root);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, _statePath, true);
    }

    private static void ValidatePackage(ScenePackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Id)) throw new InvalidDataException("Scene id is required.");
        if (string.IsNullOrWhiteSpace(package.Name)) throw new InvalidDataException("Scene name is required.");
        if (package.FormatVersion <= 0) throw new InvalidDataException("Scene formatVersion must be positive.");
        if (package.Type is SceneProjectType.Image or SceneProjectType.Video or SceneProjectType.Web && string.IsNullOrWhiteSpace(package.Entry))
            throw new InvalidDataException("This scene type requires an entry file.");
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static string SanitizeFolderName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Trim();
    }
}

public sealed class SceneLibraryState
{
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ScenePlaylist> Playlists { get; set; } = [];
    public List<DesktopSceneProfile> Profiles { get; set; } = [];
    public List<SceneApplicationRule> ApplicationRules { get; set; } = [];
    public string? ActiveProfileId { get; set; }
    public Dictionary<string, Dictionary<string, object?>> SceneProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
