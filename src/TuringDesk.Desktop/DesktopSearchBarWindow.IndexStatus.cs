using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    private readonly DispatcherTimer _indexStatusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private bool _indexStatusStarted;
    private bool _indexReadyNotified;
    private bool _indexFailureNotified;
    private bool _lastAppSearchReady;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_indexStatusStarted) return;
        _indexStatusStarted = true;
        _lastAppSearchReady = _searchIndex.AppSearchReady;

        _indexStatusTimer.Tick += IndexStatusTimer_Tick;
        RefreshIndexStatus();

        if (_searchIndex.AppSearchInitializationComplete)
            return;

        // Only the cheap L1 app snapshot participates in startup readiness.
        // Everything is intentionally demand-driven and must not keep a perpetual
        // startup poll alive just because the user has never searched for a file.
        ShellNotificationService.Publish(
            "本地搜索后台预热",
            $"{_searchIndex.AppSearchStatus}；Everything 文件搜索将在首次需要时启动。",
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
        var appJustBecameReady = !_lastAppSearchReady && appReady;
        _lastAppSearchReady = appReady;

        if (appReady)
        {
            PlaceholderText.Text = $"应用 {_searchIndex.AppCount:N0} · Everything 文件搜索按需 · 搜索或直接问 AI…";
            _indexStatusTimer.Stop();
            NotifySearchReady();
        }
        else if (_searchIndex.AppSearchInitializationComplete)
        {
            PlaceholderText.Text = $"{_searchIndex.AppSearchStatus} · Everything 文件搜索按需 · AI 仍可使用";
            _indexStatusTimer.Stop();

            if (!_indexFailureNotified)
            {
                _indexFailureNotified = true;
                ShellNotificationService.Publish(
                    "应用搜索未就绪",
                    _searchIndex.AppSearchStatus,
                    "warning");
            }
        }
        else
        {
            PlaceholderText.Text = $"{_searchIndex.AppSearchStatus} · Everything 文件搜索按需";
        }

        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (appJustBecameReady && !string.IsNullOrWhiteSpace(SearchBox.Text))
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
            "应用搜索已就绪",
            $"应用 {_searchIndex.AppCount:N0} 个已进入内存；文件搜索使用 {_searchIndex.FileSearchProviderName}，首次查询时按需启动。",
            "search");
    }
}
