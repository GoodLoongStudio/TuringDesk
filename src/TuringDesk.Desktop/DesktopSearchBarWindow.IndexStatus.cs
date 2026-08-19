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

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_indexStatusStarted) return;
        _indexStatusStarted = true;

        _indexStatusTimer.Tick += IndexStatusTimer_Tick;
        RefreshIndexStatus();

        if (_searchIndex.UsesEverything)
        {
            _indexReadyNotified = true;
            ShellNotificationService.Publish(
                "Everything 文件索引已连接",
                $"应用 {_searchIndex.AppCount:N0} 个已进入内存索引；文件搜索直接使用 Everything，不再扫描整机。",
                "search");
            return;
        }

        if (!_searchIndex.IsInitialIndexComplete)
        {
            ShellNotificationService.Publish(
                "正在建立轻量本地索引",
                $"应用 {_searchIndex.AppCount:N0} 个已可搜索；仅扫描桌面、文档和下载，不会遍历整个用户目录。",
                "search");
            _indexStatusTimer.Start();
        }
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
        if (_searchIndex.UsesEverything)
        {
            PlaceholderText.Text = "Everything 已连接 · 搜索应用、文件，或直接问 AI…";
            _indexStatusTimer.Stop();
        }
        else if (_searchIndex.IsInitialIndexComplete)
        {
            PlaceholderText.Text = "搜索应用、文件，或直接问 AI…";
            _indexStatusTimer.Stop();

            if (!_indexReadyNotified)
            {
                _indexReadyNotified = true;
                ShellNotificationService.Publish(
                    "轻量本地索引已就绪",
                    $"应用 {_searchIndex.AppCount:N0} · 本地文件 {_searchIndex.IndexedFileCount:N0}",
                    "search");
            }
        }
        else
        {
            PlaceholderText.Text =
                $"正在建立轻量索引… 应用 {_searchIndex.AppCount:N0} · 文件 {_searchIndex.IndexedFileCount:N0}";
        }

        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
