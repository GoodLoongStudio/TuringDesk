using System.IO;
using System.Windows.Media;
using TuringDesk.Desktop.Services.AppSearch;

namespace TuringDesk.Desktop.Services;

public enum DesktopSearchResultKind
{
    App,
    TextFile
}

public sealed record DesktopSearchResult(
    string Name,
    string Target,
    DesktopSearchResultKind Kind,
    string Subtitle,
    ImageSource? Icon,
    int Score,
    int Level = 1)
{
    public string KindLabel => Kind == DesktopSearchResultKind.App ? "应用" : "文件";
}

/// <summary>
/// Search coordinator only:
/// L1 delegates application discovery/ranking to AppSearchProvider.
/// L2 delegates global filename/path indexing and querying to Everything.
/// The coordinator never crawls disks and never starts Node/Harness.
/// </summary>
public sealed class DesktopSearchIndexService : IDisposable
{
    private readonly AppSearchProvider _apps = new();
    private readonly EverythingFileSearchProvider _everything = new();
    private int _disposed;

    public DesktopSearchIndexService()
        : this(initializeFileSearch: true)
    {
    }

    internal DesktopSearchIndexService(bool initializeFileSearch)
    {
        if (initializeFileSearch)
            _ = _everything.InitializeAsync();
    }

    public bool IsInitialIndexComplete => _apps.InitializationCompleted && _everything.InitializationCompleted;
    public bool AppSearchReady => _apps.IsReady;
    public bool UsesEverything => _everything.IsReady;
    public bool FileSearchReady => _everything.IsReady;
    public string AppSearchStatus => _apps.Status;
    public string FileSearchProviderName => _everything.ProviderName;
    public string FileSearchStatus => _everything.Status;
    public int AppCount => _apps.Count;
    internal Task AppSearchInitialization => _apps.Initialization;

    // Compatibility property retained for the existing status UI. TuringDesk owns
    // no filename database; Everything owns the file index.
    public int IndexedFileCount => 0;

    /// <summary>
    /// Level 1: RAM-only application search. Discovery is performed asynchronously
    /// and results come from an immutable snapshot.
    /// </summary>
    public IReadOnlyList<DesktopSearchResult> SearchApps(string query, int limit = 5)
    {
        return _apps.Search(query, limit)
            .Select(hit => new DesktopSearchResult(
                hit.Name,
                hit.Target,
                DesktopSearchResultKind.App,
                BuildAppSubtitle(hit.Category, hit.Source),
                null,
                hit.Score,
                Level: 1))
            .ToArray();
    }

    /// <summary>
    /// Level 2: bounded Everything query. Everything maintains the global filename
    /// index; TuringDesk only asks for the small number of rows visible in the UI.
    /// Single-character queries stay on the RAM-only app tier to avoid turning
    /// every first keystroke into IPC and UI churn.
    /// </summary>
    public async Task<IReadOnlyList<DesktopSearchResult>> SearchFilesAsync(
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length < 2)
            return Array.Empty<DesktopSearchResult>();

        limit = Math.Clamp(limit, 1, 16);
        var paths = await _everything.SearchAsync(query, Math.Min(16, limit * 2), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return BuildEverythingResults(paths, normalized, limit);
    }

    public async Task<IReadOnlyList<DesktopSearchResult>> SearchAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var apps = SearchApps(query, Math.Min(limit, 5));
        var files = await SearchFilesAsync(query, Math.Min(limit, 8), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return apps
            .Concat(files)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Level)
            .ThenBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 24))
            .ToArray();
    }

    public bool Open(DesktopSearchResult result)
    {
        if (result.Kind == DesktopSearchResultKind.App &&
            result.Target.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
        {
            return PackagedAppLauncher.TryLaunch(result.Target["aumid:".Length..]);
        }

        return ShellSurfaceCatalog.OpenTarget(result.Target);
    }

    public bool OpenContainingFolder(DesktopSearchResult result) =>
        result.Kind == DesktopSearchResultKind.TextFile && ShellSurfaceCatalog.OpenContainingFolder(result.Target);

    private static IReadOnlyList<DesktopSearchResult> BuildEverythingResults(
        IReadOnlyList<string> paths,
        string normalizedQuery,
        int limit)
    {
        var results = new List<DesktopSearchResult>(limit);
        foreach (var path in paths)
        {
            if (results.Count >= limit) break;
            if (string.IsNullOrWhiteSpace(path)) continue;

            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var normalizedName = NormalizeQuery(name);
            var normalizedPath = NormalizeQuery(path);
            var score = ScoreFile(normalizedQuery, normalizedName, normalizedPath);
            if (score <= 0) score = 700;

            results.Add(new DesktopSearchResult(
                name,
                path,
                DesktopSearchResultKind.TextFile,
                "Everything · " + BuildFileSubtitle(path),
                null,
                score,
                Level: 2));
        }

        return results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static string BuildAppSubtitle(string category, string source)
    {
        if (string.IsNullOrWhiteSpace(category)) return "已安装应用";
        if (string.IsNullOrWhiteSpace(source) || category.Equals(source, StringComparison.OrdinalIgnoreCase))
            return category;
        return $"{category} · {source}";
    }

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static int ScoreFile(string query, string normalizedName, string normalizedPath)
    {
        if (normalizedName.Equals(query, StringComparison.Ordinal)) return 1200;
        if (normalizedName.StartsWith(query, StringComparison.Ordinal)) return 950;
        if (normalizedName.Contains(query, StringComparison.Ordinal)) return 720;
        if (normalizedPath.Contains(query, StringComparison.Ordinal)) return 480;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1 && tokens.All(token => normalizedName.Contains(token, StringComparison.Ordinal)))
            return 620;
        if (tokens.Length > 1 && tokens.All(token => normalizedPath.Contains(token, StringComparison.Ordinal)))
            return 360;

        return 0;
    }

    private static string BuildFileSubtitle(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && directory.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            directory = "~" + directory[userProfile.Length..];

        return string.IsNullOrWhiteSpace(extension)
            ? directory
            : $"{extension} · {directory}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _apps.Dispose();
        _everything.Dispose();
    }
}
