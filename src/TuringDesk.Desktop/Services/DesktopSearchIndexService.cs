using System.Collections.Concurrent;
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
/// Tiered desktop search.
/// Level 1 is an always-resident RAM app/pinyin prefix index.
/// Level 2 delegates global filename search to voidtools Everything when available.
/// When Everything is unavailable, TuringDesk keeps only a small bounded fallback
/// snapshot of Desktop/Documents/Downloads; it never crawls the whole user profile.
/// </summary>
public sealed class DesktopSearchIndexService : IDisposable
{
    private const int MaxIndexedAliasLength = 48;
    private const int MaxFilePrefixLength = 4;
    private const int MaxLocalIndexedFiles = 6000;
    private const int MaxLocalDepth = 2;
    private const int LocalYieldInterval = 64;

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
    private readonly EverythingFileSearchProvider _everything = new();
    private readonly ConcurrentDictionary<string, IndexedTextFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _filePrefixIndex =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string[] _roots;
    private readonly Task _initialIndexTask;
    private int _disposed;

    public DesktopSearchIndexService()
    {
        _apps = ShellSurfaceCatalog.LoadStartApps(includeIcons: false)
            .Select(CreateIndexedApp)
            .ToArray();
        _appPrefixIndex = BuildAppPrefixIndex(_apps);

        _roots = BuildRoots();

        // Everything already owns the global filename/path database and keeps it
        // current through NTFS metadata. Do not duplicate that index inside the
        // desktop process. The small fallback is built only when Everything is not
        // currently queryable.
        if (_everything.IsAvailable)
        {
            _initialIndexTask = Task.CompletedTask;
        }
        else
        {
            foreach (var root in _roots)
                TryStartWatcher(root);

            _initialIndexTask = Task.Run(() => BuildInitialIndex(_lifetime.Token), _lifetime.Token);
        }
    }

    public bool IsInitialIndexComplete => _initialIndexTask.IsCompleted;
    public bool UsesEverything => _everything.IsAvailable;
    public string FileSearchProviderName => _everything.ProviderName;
    public int AppCount => _apps.Length;
    public int IndexedFileCount => _files.Count;

    /// <summary>
    /// Level 1: synchronous RAM-only application search.
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
    /// Level 2: Everything first. Global filename search never performs a TuringDesk
    /// disk crawl. The bounded RAM fallback is used only when Everything is absent.
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

        if (_everything.IsAvailable)
        {
            var paths = await _everything.SearchAsync(query, Math.Min(16, limit * 2), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var everythingResults = BuildEverythingResults(paths, normalized, limit);
            if (everythingResults.Count > 0)
                return everythingResults;
        }

        return await Task.Run<IReadOnlyList<DesktopSearchResult>>(
                () => SearchFilesCore(normalized, limit, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
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
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) continue;

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

    private IReadOnlyList<DesktopSearchResult> SearchFilesCore(
        string normalized,
        int limit,
        CancellationToken cancellationToken)
    {
        var best = new PriorityQueue<DesktopSearchResult, int>();
        var candidates = ResolveFileCandidates(normalized);
        var visited = 0;

        foreach (var file in candidates)
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

    private IEnumerable<IndexedTextFile> ResolveFileCandidates(string normalizedQuery)
    {
        var compact = NormalizeAlias(normalizedQuery);
        if (compact.Length > 0)
        {
            var prefixLength = Math.Min(MaxFilePrefixLength, compact.Length);
            for (var length = prefixLength; length >= 1; length--)
            {
                var prefix = compact[..length];
                if (!_filePrefixIndex.TryGetValue(prefix, out var bucket) || bucket.IsEmpty)
                    continue;

                return bucket.Keys
                    .Select(path => _files.TryGetValue(path, out var file) ? file : null)
                    .Where(file => file is not null)
                    .Select(file => file!);
            }
        }

        return _files.Values;
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
        var visited = 0;
        foreach (var root in _roots)
        {
            if (cancellationToken.IsCancellationRequested || _files.Count >= MaxLocalIndexedFiles) return;
            IndexTree(root, cancellationToken, ref visited);
        }
    }

    private void IndexTree(string root, CancellationToken cancellationToken, ref int visited)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0 &&
               !cancellationToken.IsCancellationRequested &&
               _files.Count < MaxLocalIndexedFiles)
        {
            var (directory, depth) = pending.Pop();
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
                    if (cancellationToken.IsCancellationRequested || _files.Count >= MaxLocalIndexedFiles) return;

                    visited++;
                    if (visited % LocalYieldInterval == 0)
                        Thread.Sleep(1);

                    if (Directory.Exists(entry))
                    {
                        if (depth < MaxLocalDepth && !ShouldSkipDirectory(entry))
                            pending.Push((entry, depth + 1));
                        continue;
                    }

                    TryIndexFile(entry);
                }
            }
            catch
            {
                // Directory contents can change while they are being enumerated.
            }
        }
    }

    private void TryStartWatcher(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                // Never attach one recursive watcher to the whole user tree. These
                // watchers only keep the small fallback roots fresh.
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 8 * 1024,
                EnableRaisingEvents = true
            };

            watcher.Created += (_, e) =>
            {
                if (File.Exists(e.FullPath)) TryIndexFile(e.FullPath);
            };
            watcher.Deleted += (_, e) => RemoveIndexedFile(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                RemoveIndexedFile(e.OldFullPath);
                if (File.Exists(e.FullPath)) TryIndexFile(e.FullPath);
            };

            _watchers.Add(watcher);
        }
        catch
        {
            // The bounded snapshot remains usable if notifications are unavailable.
        }
    }

    private void TryIndexFile(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsTextLike(path)) return;
            if (_files.Count >= MaxLocalIndexedFiles && !_files.ContainsKey(path)) return;

            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                info.Attributes.HasFlag(FileAttributes.System) ||
                info.Attributes.HasFlag(FileAttributes.Offline))
                return;

            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) return;

            var indexed = new IndexedTextFile(
                name,
                path,
                BuildFileSubtitle(path),
                NormalizeQuery(name),
                NormalizeQuery(path),
                NormalizeAlias(name));

            if (_files.TryGetValue(path, out var previous))
                UnindexFilePrefixes(previous);

            _files[path] = indexed;
            IndexFilePrefixes(indexed);
        }
        catch
        {
            // Files can disappear or become inaccessible between notifications.
        }
    }

    private void RemoveIndexedFile(string path)
    {
        if (!_files.TryRemove(path, out var removed)) return;
        UnindexFilePrefixes(removed);
    }

    private void IndexFilePrefixes(IndexedTextFile file)
    {
        if (string.IsNullOrWhiteSpace(file.CompactName)) return;
        var max = Math.Min(MaxFilePrefixLength, file.CompactName.Length);
        for (var length = 1; length <= max; length++)
        {
            var prefix = file.CompactName[..length];
            var bucket = _filePrefixIndex.GetOrAdd(
                prefix,
                static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            bucket[file.Path] = 0;
        }
    }

    private void UnindexFilePrefixes(IndexedTextFile file)
    {
        if (string.IsNullOrWhiteSpace(file.CompactName)) return;
        var max = Math.Min(MaxFilePrefixLength, file.CompactName.Length);
        for (var length = 1; length <= max; length++)
        {
            var prefix = file.CompactName[..length];
            if (!_filePrefixIndex.TryGetValue(prefix, out var bucket)) continue;
            bucket.TryRemove(file.Path, out _);
            if (bucket.IsEmpty)
                _filePrefixIndex.TryRemove(prefix, out _);
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
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            string.IsNullOrWhiteSpace(userProfile) ? string.Empty : Path.Combine(userProfile, "Downloads")
        };

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
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
        string NormalizedPath,
        string CompactName);
}
