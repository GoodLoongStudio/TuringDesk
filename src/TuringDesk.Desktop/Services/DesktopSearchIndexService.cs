using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;

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
    int Score)
{
    public string KindLabel => Kind == DesktopSearchResultKind.App ? "应用" : "文件";
}

/// <summary>
/// Fast, user-space search index for the desktop search bar.
///
/// The query path is deliberately Everything-like: build a compact in-memory
/// filename/path index once, then keep it fresh with FileSystemWatcher instead
/// of walking the disk for every keystroke. The first release indexes text/code
/// files under the current user's profile plus Start Menu applications. This is
/// intentionally non-elevated; a future NTFS MFT/USN provider can plug into the
/// same result contract without changing the search UI.
/// </summary>
public sealed class DesktopSearchIndexService : IDisposable
{
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

    private readonly IReadOnlyList<StartAppItem> _apps;
    private readonly ConcurrentDictionary<string, IndexedTextFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string[] _roots;
    private readonly Task _initialIndexTask;
    private int _disposed;

    public DesktopSearchIndexService()
    {
        // This constructor is normally called on the WPF UI thread, which keeps
        // shell icon extraction compatible with the existing Start Menu catalog.
        _apps = ShellSurfaceCatalog.LoadStartApps();
        _roots = BuildRoots();

        foreach (var root in _roots)
            TryStartWatcher(root);

        _initialIndexTask = Task.Run(() => BuildInitialIndex(_lifetime.Token), _lifetime.Token);
    }

    public bool IsInitialIndexComplete => _initialIndexTask.IsCompleted;

    public Task<IReadOnlyList<DesktopSearchResult>> SearchAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0)
            return Task.FromResult<IReadOnlyList<DesktopSearchResult>>(Array.Empty<DesktopSearchResult>());

        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<DesktopSearchResult>(Math.Max(12, limit * 2));

        foreach (var app in _apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = Score(normalized, app.Name, app.Target, appBoost: 75);
            if (score <= 0) continue;

            results.Add(new DesktopSearchResult(
                app.Name,
                app.Target,
                DesktopSearchResultKind.App,
                string.IsNullOrWhiteSpace(app.Category) ? "已安装应用" : app.Category,
                app.Icon,
                score));
        }

        foreach (var file in _files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = Score(normalized, file.Name, file.Path, appBoost: 0);
            if (score <= 0) continue;

            results.Add(new DesktopSearchResult(
                file.Name,
                file.Path,
                DesktopSearchResultKind.TextFile,
                file.Subtitle,
                null,
                score));
        }

        IReadOnlyList<DesktopSearchResult> ordered = results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 24))
            .ToArray();

        return Task.FromResult(ordered);
    }

    public bool Open(DesktopSearchResult result) => ShellSurfaceCatalog.OpenTarget(result.Target);

    public bool OpenContainingFolder(DesktopSearchResult result) =>
        result.Kind == DesktopSearchResultKind.TextFile && ShellSurfaceCatalog.OpenContainingFolder(result.Target);

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
                // The watcher will catch later additions and the next session can
                // rebuild any entries missed during this pass.
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
            // Search remains useful from the initial snapshot even if a watched
            // folder is unavailable or refuses notification handles.
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
                BuildFileSubtitle(path));
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

        // Index the whole user profile so developer project folders are included,
        // but explicitly skip AppData, package caches and source-control internals.
        if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
            roots.Add(userProfile);

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static int Score(string query, string name, string path, int appBoost)
    {
        var nameValue = name.ToLowerInvariant();
        var pathValue = path.ToLowerInvariant();

        if (nameValue.Equals(query, StringComparison.Ordinal)) return 1200 + appBoost;
        if (nameValue.StartsWith(query, StringComparison.Ordinal)) return 950 + appBoost;
        if (nameValue.Contains(query, StringComparison.Ordinal)) return 720 + appBoost;
        if (pathValue.Contains(query, StringComparison.Ordinal)) return 480 + appBoost;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1 && tokens.All(token => nameValue.Contains(token, StringComparison.Ordinal)))
            return 620 + appBoost;
        if (tokens.Length > 1 && tokens.All(token => pathValue.Contains(token, StringComparison.Ordinal)))
            return 360 + appBoost;

        return 0;
    }

    private static string BuildFileSubtitle(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && directory.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            directory = "~" + directory[userProfile.Length..];
        }

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

    private sealed record IndexedTextFile(string Name, string Path, string Subtitle);
}
