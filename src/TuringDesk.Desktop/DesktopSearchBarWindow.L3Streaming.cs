using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    private readonly object _l3StreamGate = new();
    private DispatcherTimer? _l3StreamRenderTimer;
    private string? _pendingL3Partial;
    private string? _renderedL3Partial;
    private int _l3StreamStartScheduled;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        _l3StreamRenderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _l3StreamRenderTimer.Tick += L3StreamRenderTimer_Tick;

        _quickAnswer.PartialResponseUpdated += OnL3PartialResponseUpdated;
        PreviewKeyDown += L3Window_PreviewKeyDown;
        Closed += L3Streaming_Closed;
    }

    private void L3Streaming_Closed(object? sender, EventArgs e)
    {
        _quickAnswer.PartialResponseUpdated -= OnL3PartialResponseUpdated;
        PreviewKeyDown -= L3Window_PreviewKeyDown;

        if (_l3StreamRenderTimer is not null)
        {
            _l3StreamRenderTimer.Stop();
            _l3StreamRenderTimer.Tick -= L3StreamRenderTimer_Tick;
            _l3StreamRenderTimer = null;
        }
    }

    private void L3Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_busy || e.Key != Key.Escape) return;

        // SearchBox is disabled while a request is running, so handling Escape on
        // the Window is required for cancellation to remain reachable during L3.
        e.Handled = true;
        _activeRequest?.Cancel();
        RestoreIdleState(clearText: false);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnL3PartialResponseUpdated(string partial)
    {
        if (string.IsNullOrEmpty(partial)) return;

        lock (_l3StreamGate)
            _pendingL3Partial = partial;

        // Provider streams can produce many tiny chunks. Schedule the UI timer once
        // and let it coalesce them to at most ~20 WPF renders per second.
        if (Interlocked.Exchange(ref _l3StreamStartScheduled, 1) != 0) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _l3StreamStartScheduled, 0);
            if (!_busy || !IsVisible) return;

            RenderPendingL3Partial();
            _l3StreamRenderTimer?.Start();
        }), DispatcherPriority.Background);
    }

    private void L3StreamRenderTimer_Tick(object? sender, EventArgs e)
    {
        if (!_busy || !IsVisible)
        {
            _l3StreamRenderTimer?.Stop();
            return;
        }

        RenderPendingL3Partial();
    }

    private void RenderPendingL3Partial()
    {
        string? partial;
        lock (_l3StreamGate)
            partial = _pendingL3Partial;

        if (string.IsNullOrEmpty(partial) || string.Equals(partial, _renderedL3Partial, StringComparison.Ordinal))
            return;

        _renderedL3Partial = partial;
        ReplyTitle.Text = "CLI · 回答中";
        ReplyText.Text = partial;
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(127, 143, 255));
        DeepProcessButton.Visibility = Visibility.Collapsed;
        CollapseSearchResults();
        ExpandReply(CalculateStreamingReplyHeight(partial));
    }

    private static double CalculateStreamingReplyHeight(string text)
    {
        if (text.Length <= 260) return 150;
        if (text.Length <= 900) return 190;
        return 230;
    }
}
