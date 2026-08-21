using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    private readonly DispatcherTimer _indexStatusTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private bool _indexStatusStarted;
    private bool _indexReadyNotified;
    private bool _indexFailureNotified;
    private bool _lastAppSearchReady;
    private bool _lastFileSearchReady;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_indexStatusStarted) return;
        _indexStatusStarted = true;
        _lastAppSearchReady = _searchIndex.AppSearchReady;
        _lastFileSearchReady = _searchIndex.FileSearchReady;

        _indexStatusTimer.Tick += IndexStatusTimer_Tick;
        RefreshIndexStatus();

        if (_searchIndex.AppSearchReady && _searchIndex.FileSearchReady)
        {
            NotifySearchReady();
            return;
        }

        ShellNotificationService.Publish(
            "正在初始化本地搜索",
            $"{_searchIndex.AppSearchStatus}；{_searchIndex.FileSearchStatus}。应用发现参考 PowerToys，文件索引由 Everything 负责。",
            "search");
        _indexStatusTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _indexStatusTimer.Stop();
        _indexStatusTimer.Tick -= IndexStatusTimer_Tick;
        base.OnClosed(e);
    }

    private void IndexStatusTimer_Tick(object? sender, EventArgs e) => RefreshIndexStatus();

    private void RefreshIndexStatus()
    {
        var appReady = _searchIndex.AppSearchReady;
        var fileReady = _searchIndex.FileSearchReady;
        var appJustBecameReady = !_lastAppSearchReady && appReady;
        var fileJustBecameReady = !_lastFileSearchReady && fileReady;
        _lastAppSearchReady = appReady;
        _lastFileSearchReady = fileReady;

        if (appReady && fileReady)
        {
            PlaceholderText.Text = $"应用 {_searchIndex.AppCount:N0} · Everything 已连接 · 搜索或直接问 AI…";
            _indexStatusTimer.Stop();
            NotifySearchReady();
        }
        else if (_searchIndex.IsInitialIndexComplete)
        {
            PlaceholderText.Text = appReady
                ? $"应用搜索已就绪 · {_searchIndex.FileSearchStatus} · AI 仍可使用"
                : $"{_searchIndex.AppSearchStatus} · Everything 文件搜索可用 · AI 仍可使用";

            // Everything can be started, restarted or finish its own indexing after
            // TuringDesk's first probe completed. Keep a low-frequency status poll so
            // the search bar can recover automatically instead of remaining stuck on
            // "未就绪" for the rest of the desktop session.
            _indexStatusTimer.Interval = TimeSpan.FromSeconds(2);
            if (!_indexStatusTimer.IsEnabled)
                _indexStatusTimer.Start();

            if (!_indexFailureNotified)
            {
                _indexFailureNotified = true;
                ShellNotificationService.Publish(
                    "本地搜索部分能力未就绪",
                    $"{_searchIndex.AppSearchStatus}；{_searchIndex.FileSearchStatus}",
                    "warning");
            }
        }
        else
        {
            _indexStatusTimer.Interval = TimeSpan.FromMilliseconds(400);
            PlaceholderText.Text = $"{_searchIndex.AppSearchStatus} · {_searchIndex.FileSearchStatus}";
        }

        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if ((appJustBecameReady || fileJustBecameReady) && !string.IsNullOrWhiteSpace(SearchBox.Text))
            RefreshCurrentQueryAfterIndexReady();
    }

    private void RefreshCurrentQueryAfterIndexReady()
    {
        CancelSearchPipeline();
        var generation = Interlocked.Increment(ref _searchGeneration);
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        var apps = _searchIndex.SearchApps(query, 5);
        ApplySearchResults(query, generation, apps);

        var pipeline = new CancellationTokenSource();
        _searchPipeline = pipeline;
        _ = StreamFileResultsAsync(query, apps, generation, pipeline);
    }

    private void NotifySearchReady()
    {
        if (_indexReadyNotified) return;
        _indexReadyNotified = true;
        ShellNotificationService.Publish(
            "本地搜索已就绪",
            $"应用 {_searchIndex.AppCount:N0} 个在内存中；文件查询使用 {_searchIndex.FileSearchProviderName}。",
            "search");
    }
}
