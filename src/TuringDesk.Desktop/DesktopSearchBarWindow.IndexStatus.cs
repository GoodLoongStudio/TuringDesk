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

        if (!_searchIndex.IsInitialIndexComplete)
        {
            ShellNotificationService.Publish(
                "正在建立本地搜索索引",
                $"应用 {_searchIndex.AppCount:N0} 个已可搜索；文件索引在后台渐进建立，不会阻塞桌面。",
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
        var complete = _searchIndex.IsInitialIndexComplete;
        if (complete)
        {
            PlaceholderText.Text = "搜索应用、文件，或直接问 AI…";
            _indexStatusTimer.Stop();

            if (!_indexReadyNotified)
            {
                _indexReadyNotified = true;
                ShellNotificationService.Publish(
                    "本地搜索索引已就绪",
                    $"应用 {_searchIndex.AppCount:N0} · 文件 {_searchIndex.IndexedFileCount:N0}",
                    "search");
            }
        }
        else
        {
            PlaceholderText.Text =
                $"正在建立本地索引… 应用 {_searchIndex.AppCount:N0} · 文件 {_searchIndex.IndexedFileCount:N0}";
        }

        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
