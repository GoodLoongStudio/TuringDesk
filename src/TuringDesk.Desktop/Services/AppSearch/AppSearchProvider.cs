namespace TuringDesk.Desktop.Services.AppSearch;

internal sealed record AppSearchHit(
    string Name,
    string Target,
    string Category,
    string Source,
    int Score);

/// <summary>
/// Immutable-snapshot application search provider. Discovery and refresh are
/// performed off the UI thread; queries only read a RAM snapshot and never touch
/// the shell, registry or filesystem.
/// </summary>
internal sealed class AppSearchProvider : IDisposable
{
    private readonly ProgramDiscoveryService _discovery = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _refreshDebounce;
    private readonly Timer _periodicRefresh;
    private AppSearchEntry[] _snapshot = [];
    private Task _initializationTask;
    private volatile bool _isReady;
    private volatile string _status = "正在建立应用索引…";
    private int _disposed;

    public AppSearchProvider()
    {
        _refreshDebounce = new Timer(_ => QueueRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        _periodicRefresh = new Timer(_ => QueueRefresh(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        StartWatchers();
        _initializationTask = Task.Run(() => RefreshCoreAsync(_lifetime.Token));
    }

    public bool IsReady => _isReady;
    public int Count => Volatile.Read(ref _snapshot).Length;
    public string Status => _status;
    public Task Initialization => _initializationTask;

    public IReadOnlyList<AppSearchHit> Search(string query, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<AppSearchHit>();
        limit = Math.Clamp(limit, 1, 12);

        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Length == 0) return Array.Empty<AppSearchHit>();

        var ranked = snapshot
            .Select(entry => new AppSearchHit(
                entry.Name,
                entry.Target,
                entry.Category,
                entry.Source,
                AppSearchMatcher.Score(query, entry) + SourcePriority(entry.Source)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Name, StringComparer.CurrentCultureIgnoreCase);

        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var results = new List<AppSearchHit>(limit);
        foreach (var hit in ranked)
        {
            if (!seenNames.Add(hit.Name)) continue;
            results.Add(hit);
            if (results.Count >= limit) break;
        }

        return results;
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _status = _isReady ? "正在刷新应用索引…" : "正在建立应用索引…";

            var programs = await Task.Run(_discovery.Discover, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var entries = programs
                .Select(AppSearchEntry.Create)
                .Where(entry => entry.Aliases.Count > 0)
                .ToArray();

            Volatile.Write(ref _snapshot, entries);
            _isReady = true;
            _status = $"应用索引已就绪 · {entries.Length:N0} 个";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status = "应用索引已停止";
        }
        catch (Exception error)
        {
            // Preserve the last known-good immutable snapshot if refresh fails.
            _status = _isReady
                ? $"应用索引刷新失败，继续使用缓存：{error.Message}"
                : $"应用索引初始化失败：{error.Message}";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void QueueRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshCoreAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        });
    }

    private void StartWatchers()
    {
        foreach (var root in _discovery.WatchRoots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 8 * 1024,
                    EnableRaisingEvents = true
                };

                FileSystemEventHandler changed = (_, _) => ScheduleRefresh();
                RenamedEventHandler renamed = (_, _) => ScheduleRefresh();
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Changed += changed;
                watcher.Renamed += renamed;
                _watchers.Add(watcher);
            }
            catch
            {
                // Periodic refresh remains as the low-frequency fallback.
            }
        }
    }

    private void ScheduleRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _refreshDebounce.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan); } catch { }
    }

    private static int SourcePriority(string source) => source switch
    {
        "System" => 90,
        "任务栏固定" => 70,
        "桌面快捷方式" or "公共桌面快捷方式" => 55,
        "开始菜单" or "公共开始菜单" => 45,
        "AppsFolder" => 35,
        _ => 20
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();

        foreach (var watcher in _watchers)
        {
            try { watcher.Dispose(); } catch { }
        }
        _watchers.Clear();

        try { _refreshDebounce.Dispose(); } catch { }
        try { _periodicRefresh.Dispose(); } catch { }
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }
}
