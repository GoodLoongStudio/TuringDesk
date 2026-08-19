using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using NPinyin;

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
/// Tiered desktop search index.
///
/// Level 1 is a startup-built RAM index for installed applications. The hot path
/// never touches disk and supports the original app name, compact full pinyin and
/// pinyin initials through a prefix hash index.
///
/// Level 2 is the user-profile text/code file index. FileSystemWatcher keeps the
/// snapshot incrementally fresh, while query scoring is always executed on a pool
/// thread so a large profile cannot block the WPF dispatcher.
/// </summary>
public sealed class DesktopSearchIndexService : IDisposable
{
    private const int MaxIndexedAliasLength = 48;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv",
        ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".cs", ".csx", ".fs", ".fsx", ".vb",
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".py", ".pyw", ".rs", ".go", ".java", ".kt", ".kts", ".swift",
        ".sql", ".graphql", ".gql", ".html", ".htm", ".css", ".scss", ".less",
        ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".zsh", ".fish",
        ".gitignore", ".gitattributes", ".editorconfig", ".env"
    };

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "$Recycle.Bin", "System Volume Information",
        ".git", ".svn", ".hg", ".vs", ".idea",
        "node_modules", "packages", ".nuget", ".cache"
    };

    private readonly IndexedApp[] _apps;
    private readonly IReadOnlyDictionary<string, int[]> _appPrefixIndex;
    private readonly ConcurrentDictionary<string, IndexedTextFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string[] _roots;
    private readonly Task _initialIndexTask;
    private int _disposed;

    public DesktopSearchIndexService()
    {
        // ShellSurfaceCatalog icon extraction is intentionally performed here,
        // normally on the WPF UI thread. After construction the L1 query path is
        // pure RAM access and never invokes Shell/COM again.
        _apps = ShellSurfaceCatalog.LoadStartApps()
            .Select(CreateIndexedApp)
            .ToArray();
        _appPrefixIndex = BuildAppPrefixIndex(_apps);

        _roots = BuildRoots();
        foreach (var root in _roots)
            TryStartWatcher(root);

        _initialIndexTask = Task.Run(() => BuildInitialIndex(_lifetime.Token), _lifetime.Token);
    }

    public bool IsInitialIndexComplete => _initialIndexTask.IsCompleted;
    public int AppCount => _apps.Length;
    public int IndexedFileCount => _files.Count;

    /// <summary>
    /// Level 1: synchronous RAM-only application search. Safe to call directly
    /// from TextChanged before any debounce/yield.
    /// </summary>
    public IReadOnlyList<DesktopSearchResult> SearchApps(string query, int limit = 5)
    {
        var normalized = NormalizeAlias(query);
        if (normalized.Length == 0 || _apps.Length == 0)
            return Array.Empty<DesktopSearchResult>();

        limit = Math.Clamp(limit, 1, 12);
        IEnumerable<int> candidates;
        if (_appPrefixIndex.TryGetValue(normalized, out var indexedCandidates))
        {
            candidates = indexedCandidates;
        }
        else
        {
            // Contains/fuzzy fallback still stays cheap because the installed-app
            // catalog is small and every searchable alias was precomputed.
            candidates = Enumerable.Range(0, _apps.Length);
        }

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
    /// Level 2: asynchronous file search. It never enumerates the file snapshot on
    /// the caller/dispatcher thread. Cancellation is checked throughout scoring so
    /// rapid typing abandons obsolete work quickly.
    /// </summary>
    public Task<IReadOnlyList<DesktopSearchResult>> SearchFilesAsync(
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0)
            return Task.FromResult<IReadOnlyList<DesktopSearchResult>>(Array.Empty<DesktopSearchResult>());

        limit = Math.Clamp(limit, 1, 16);
        return Task.Run<IReadOnlyList<DesktopSearchResult>>(
            () => SearchFilesCore(normalized, limit, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Compatibility entry point for callers that still want one merged result.
    /// New search-bar code should render SearchApps immediately, then stream in
    /// SearchFilesAsync results.
    /// </summary>
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

    private IReadOnlyList<DesktopSearchResult> SearchFilesCore(
        string normalized,
        int limit,
        CancellationToken cancellationToken)
    {
        var best = new PriorityQueue<DesktopSearchResult, int>();
        var visited = 0;

        foreach (var file in _files.Values)
        {
            if ((visited++ & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var score = Score(normalized, file.NormalizedName, file.NormalizedPath);
            if (score <= 0) continue;

            var result = new DesktopSearchResult(
                file.Name,
                file.Path,
                DesktopSearchResultKind.TextFile,
                file.Subtitle,
                null,
                score,
                Level: 2);

            best.Enqueue(result, score);
            if (best.Count > limit)
                _ = best.Dequeue();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return best.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IndexedApp CreateIndexedApp(StartAppItem app)
    {
        var normalizedName = NormalizeAlias(app.Name);
        var pinyin = string.Empty;
        var initials = string.Empty;

        try
        {
            var converted = Pinyin.GetPinyin(app.Name) ?? string.Empty;
            var syllables = converted
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            pinyin = NormalizeAlias(string.Concat(syllables));
            initials = NormalizeAlias(string.Concat(syllables.Select(item => item.Length > 0 ? item[0] : '\0')));
        }
        catch
        {
            // A malformed/special shell display name must never prevent the whole
            // launcher index from being constructed.
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

    private void BuildInitialIndex(CancellationToken cancellationToken)
    {
        foreach (var root in _roots)
        {
            if (cancellationToken.IsCancellationRequested) return;
            IndexTree(root, cancellationToken);
        }
    }

    private void IndexTree(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    if (Directory.Exists(entry))
                    {
                        if (!ShouldSkipDirectory(entry)) pending.Push(entry);
                        continue;
                    }

                    TryIndexFile(entry);
                }
            }
            catch
            {
                // Directory contents may change while they are being enumerated.
                // Watchers catch subsequent updates and the next session rebuilds
                // any entries missed during this pass.
            }
        }
    }

    private void TryStartWatcher(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };

            watcher.Created += (_, e) =>
            {
                if (File.Exists(e.FullPath)) TryIndexFile(e.FullPath);
            };
            watcher.Deleted += (_, e) => _files.TryRemove(e.FullPath, out _);
            watcher.Renamed += (_, e) =>
            {
                _files.TryRemove(e.OldFullPath, out _);
                if (File.Exists(e.FullPath)) TryIndexFile(e.FullPath);
            };
            watcher.Error += (_, _) =>
            {
                if (!_lifetime.IsCancellationRequested)
                    _ = Task.Run(() => IndexTree(root, _lifetime.Token), _lifetime.Token);
            };

            _watchers.Add(watcher);
        }
        catch
        {
            // Search remains useful from the initial snapshot if notifications
            // are unavailable for a specific root.
        }
    }

    private void TryIndexFile(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsTextLike(path)) return;
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                info.Attributes.HasFlag(FileAttributes.System) ||
                info.Attributes.HasFlag(FileAttributes.Offline))
                return;

            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) return;

            _files[path] = new IndexedTextFile(
                name,
                path,
                BuildFileSubtitle(path),
                NormalizeQuery(name),
                NormalizeQuery(path));
        }
        catch
        {
            // Files can disappear or become inaccessible between notifications.
        }
    }

    private static bool IsTextLike(string path)
    {
        var name = Path.GetFileName(path);
        if (TextExtensions.Contains(name)) return true;
        return TextExtensions.Contains(Path.GetExtension(path));
    }

    private static bool ShouldSkipDirectory(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (SkippedDirectoryNames.Contains(name)) return true;

            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden) ||
                   attributes.HasFlag(FileAttributes.System) ||
                   attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static string[] BuildRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
            roots.Add(userProfile);

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        _lifetime.Cancel();
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Best-effort shutdown only.
            }
        }
        _watchers.Clear();
        _lifetime.Dispose();
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

    private sealed record IndexedTextFile(
        string Name,
        string Path,
        string Subtitle,
        string NormalizedName,
        string NormalizedPath);
}
