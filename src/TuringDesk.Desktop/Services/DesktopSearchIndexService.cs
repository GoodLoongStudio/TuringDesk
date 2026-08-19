using System.IO;
using System.Windows.Media;
using PinyinNet;

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
/// Two lightweight local search tiers:
/// Level 1 keeps only installed application/shortcut aliases in RAM.
/// Level 2 delegates all filename/path indexing and querying to Everything.
///
/// TuringDesk intentionally does not enumerate user directories, maintain a file
/// database or attach recursive FileSystemWatchers. Everything owns that job.
/// </summary>
public sealed class DesktopSearchIndexService : IDisposable
{
    private const int MaxIndexedAliasLength = 48;

    private readonly IndexedApp[] _apps;
    private readonly IReadOnlyDictionary<string, int[]> _appPrefixIndex;
    private readonly EverythingFileSearchProvider _everything = new();
    private readonly Task _everythingInitialization;
    private int _disposed;

    public DesktopSearchIndexService()
    {
        _apps = ShellSurfaceCatalog.LoadStartApps(includeIcons: false)
            .Select(CreateIndexedApp)
            .ToArray();
        _appPrefixIndex = BuildAppPrefixIndex(_apps);

        // This only starts/attaches to Everything. It never scans disks in the
        // TuringDesk process and never wakes the Node/Harness runtime.
        _everythingInitialization = _everything.InitializeAsync();
    }

    public bool IsInitialIndexComplete => _everything.InitializationCompleted;
    public bool UsesEverything => _everything.IsReady;
    public bool FileSearchReady => _everything.IsReady;
    public string FileSearchProviderName => _everything.ProviderName;
    public string FileSearchStatus => _everything.Status;
    public int AppCount => _apps.Length;

    // Retained for UI/source compatibility. TuringDesk deliberately owns zero
    // filename index entries now; Everything owns the file database.
    public int IndexedFileCount => 0;

    /// <summary>
    /// Level 1: synchronous RAM-only application search. No disk/registry/IPC work.
    /// </summary>
    public IReadOnlyList<DesktopSearchResult> SearchApps(string query, int limit = 5)
    {
        var normalized = NormalizeAlias(query);
        if (normalized.Length == 0 || _apps.Length == 0)
            return Array.Empty<DesktopSearchResult>();

        limit = Math.Clamp(limit, 1, 12);
        IEnumerable<int> candidates = _appPrefixIndex.TryGetValue(normalized, out var indexedCandidates)
            ? indexedCandidates
            : Enumerable.Range(0, _apps.Length);

        return candidates
            .Select(index => (_apps[index], ScoreApp(normalized, _apps[index])))
            .Where(pair => pair.Item2 > 0)
            .OrderByDescending(pair => pair.Item2)
            .ThenBy(pair => pair.Item1.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(pair => new DesktopSearchResult(
                pair.Item1.Name,
                pair.Item1.Target,
                DesktopSearchResultKind.App,
                string.IsNullOrWhiteSpace(pair.Item1.Category) ? "已安装应用" : pair.Item1.Category,
                pair.Item1.Icon,
                pair.Item2,
                Level: 1))
            .ToArray();
    }

    /// <summary>
    /// Level 2: bounded Everything query. Everything maintains the global filename
    /// index; TuringDesk only asks for the small number of rows visible in the UI.
    /// </summary>
    public async Task<IReadOnlyList<DesktopSearchResult>> SearchFilesAsync(
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0)
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

    public bool Open(DesktopSearchResult result) => ShellSurfaceCatalog.OpenTarget(result.Target);

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
            var score = Score(normalizedQuery, normalizedName, normalizedPath);
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

    private static IndexedApp CreateIndexedApp(StartAppItem app)
    {
        var normalizedName = NormalizeAlias(app.Name);
        var pinyin = string.Empty;
        var initials = string.Empty;

        try
        {
            pinyin = NormalizeAlias(PinyinConvert.GetPinyin(app.Name) ?? string.Empty);
            initials = NormalizeAlias(PinyinConvert.GetPinyinFirstLetter(app.Name) ?? string.Empty);
        }
        catch
        {
            // One malformed shell display name must not invalidate the launcher.
        }

        return new IndexedApp(
            app.Name,
            app.Target,
            app.Category,
            app.Icon,
            normalizedName,
            pinyin,
            initials);
    }

    private static IReadOnlyDictionary<string, int[]> BuildAppPrefixIndex(IndexedApp[] apps)
    {
        var mutable = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < apps.Length; index++)
        {
            foreach (var alias in apps[index].Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var max = Math.Min(alias.Length, MaxIndexedAliasLength);
                for (var length = 1; length <= max; length++)
                {
                    var prefix = alias[..length];
                    if (!mutable.TryGetValue(prefix, out var list))
                    {
                        list = new List<int>(4);
                        mutable[prefix] = list;
                    }

                    if (list.Count == 0 || list[^1] != index)
                        list.Add(index);
                }
            }
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct().ToArray(),
            StringComparer.Ordinal);
    }

    private static int ScoreApp(string query, IndexedApp app)
    {
        var nameScore = ScoreAlias(query, app.NormalizedName, exact: 1320, prefix: 1080, contains: 760);
        var pinyinScore = ScoreAlias(query, app.Pinyin, exact: 1240, prefix: 1010, contains: 690);
        var initialsScore = ScoreAlias(query, app.Initials, exact: 1210, prefix: 990, contains: 650);
        return Math.Max(nameScore, Math.Max(pinyinScore, initialsScore));
    }

    private static int ScoreAlias(string query, string value, int exact, int prefix, int contains)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (value.Equals(query, StringComparison.Ordinal)) return exact;
        if (value.StartsWith(query, StringComparison.Ordinal)) return prefix;
        if (value.Contains(query, StringComparison.Ordinal)) return contains;
        return 0;
    }

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string NormalizeAlias(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var buffer = new char[value.Length];
        var count = 0;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                buffer[count++] = character;
        }
        return new string(buffer, 0, count);
    }

    private static int Score(string query, string normalizedName, string normalizedPath)
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
        _everything.Dispose();
    }

    private sealed record IndexedApp(
        string Name,
        string Target,
        string Category,
        ImageSource? Icon,
        string NormalizedName,
        string Pinyin,
        string Initials)
    {
        public IEnumerable<string> Aliases
        {
            get
            {
                yield return NormalizedName;
                if (!string.IsNullOrWhiteSpace(Pinyin) && !Pinyin.Equals(NormalizedName, StringComparison.Ordinal))
                    yield return Pinyin;
                if (!string.IsNullOrWhiteSpace(Initials) && !Initials.Equals(NormalizedName, StringComparison.Ordinal))
                    yield return Initials;
            }
        }
    }
}
