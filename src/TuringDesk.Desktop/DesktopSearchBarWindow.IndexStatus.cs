using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    private readonly DispatcherTimer _indexStatusTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };
    private bool _indexStatusStarted;
    private bool _indexReadyNotified;
    private bool _indexFailureNotified;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_indexStatusStarted) return;
        _indexStatusStarted = true;

        _indexStatusTimer.Tick += IndexStatusTimer_Tick;
        RefreshIndexStatus();

        if (_searchIndex.FileSearchReady)
        {
            NotifyEverythingReady();
            return;
        }

        ShellNotificationService.Publish(
            "正在启动 Everything 文件搜索",
            $"应用 {_searchIndex.AppCount:N0} 个已经可以搜索；文件名/路径索引由 Everything 负责，TuringDesk 不会扫描你的磁盘。",
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
        if (_searchIndex.FileSearchReady)
        {
            PlaceholderText.Text = "Everything 已连接 · 搜索应用、文件，或直接问 AI…";
            _indexStatusTimer.Stop();
            NotifyEverythingReady();
        }
        else if (_searchIndex.IsInitialIndexComplete)
        {
            PlaceholderText.Text = "Everything 文件搜索未就绪 · 应用搜索和 AI 仍可使用";
            _indexStatusTimer.Stop();

            if (!_indexFailureNotified)
            {
                _indexFailureNotified = true;
                ShellNotificationService.Publish(
                    "Everything 文件搜索未就绪",
                    _searchIndex.FileSearchStatus,
                    "warning");
            }
        }
        else
        {
            PlaceholderText.Text = $"正在启动 Everything… 应用 {_searchIndex.AppCount:N0} 个已可搜索";
        }

        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NotifyEverythingReady()
    {
        if (_indexReadyNotified) return;
        _indexReadyNotified = true;
        ShellNotificationService.Publish(
            "Everything 文件搜索已就绪",
            $"{_searchIndex.FileSearchProviderName} · 应用 {_searchIndex.AppCount:N0} 个在内存中；文件查询直接走 Everything。",
            "search");
    }
}
