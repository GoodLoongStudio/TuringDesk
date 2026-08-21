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
    private long _l3TimeoutWatchdogGeneration;
    private bool _l3UserCancellationRequested;

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
        if (_retryButton is not null)
            _retryButton.Click += L3RetryButton_Click;
        Closed += L3Streaming_Closed;
    }

    private void L3Streaming_Closed(object? sender, EventArgs e)
    {
        _quickAnswer.PartialResponseUpdated -= OnL3PartialResponseUpdated;
        PreviewKeyDown -= L3Window_PreviewKeyDown;
        if (_retryButton is not null)
            _retryButton.Click -= L3RetryButton_Click;
        Interlocked.Increment(ref _l3TimeoutWatchdogGeneration);

        if (_l3StreamRenderTimer is not null)
        {
            _l3StreamRenderTimer.Stop();
            _l3StreamRenderTimer.Tick -= L3StreamRenderTimer_Tick;
            _l3StreamRenderTimer = null;
        }
    }

    private void L3Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_busy &&
            e.Key == Key.Enter &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            SearchResultsList.SelectedItem is null)
        {
            StartL3TimeoutWatchdog(SearchBox.Text.Trim());
        }

        if (!_busy || e.Key != Key.Escape) return;

        // Keep the request serialized until the provider has actually unwound its
        // cancellation. Releasing _busy here would allow a second Enter/request to
        // start while the first HttpClient stream is still completing callbacks.
        e.Handled = true;
        _l3UserCancellationRequested = true;
        Interlocked.Increment(ref _l3TimeoutWatchdogGeneration);
        _activeRequest?.Cancel();
        ReplyTitle.Text = "CLI · 正在取消";
        ReplyText.Text = "正在停止当前回答…";
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        SetL3ActionState(retry: false, deep: false);
        _l3StreamRenderTimer?.Stop();
    }

    private void L3RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        StartL3TimeoutWatchdog(SearchBox.Text.Trim());
    }

    private void StartL3TimeoutWatchdog(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _l3UserCancellationRequested = false;
        var generation = Interlocked.Increment(ref _l3TimeoutWatchdogGeneration);
        _ = WatchL3TimeoutAsync(prompt, generation);
    }

    private async Task WatchL3TimeoutAsync(string prompt, long generation)
    {
        await Task.Delay(SearchResponseTimeout + TimeSpan.FromMilliseconds(250));
        if (!IsVisible || generation != Volatile.Read(ref _l3TimeoutWatchdogGeneration)) return;
        if (_l3UserCancellationRequested || !string.Equals(SearchBox.Text.Trim(), prompt, StringComparison.Ordinal)) return;

        // SubmitQuickAsync currently owns the timeout CTS. When that CTS fires it can
        // unwind through the same OperationCanceledException path as a user Escape.
        // Preserve the explicit timeout failure/retry state after that unwind instead
        // of silently collapsing the answer panel back to idle.
        if (!_busy && !string.Equals(ReplyTitle.Text, "图灵", StringComparison.Ordinal)) return;

        _activeRequest?.Cancel();
        ShowL3Failure(
            prompt,
            "CLI 响应超时",
            $"轻量回答在 {SearchResponseTimeout.TotalSeconds:0} 秒内未完成。请重试，或在模型设置中选择响应更快的模型。");
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
            if (!_busy || !IsVisible || _activeRequest?.IsCancellationRequested == true) return;

            RenderPendingL3Partial();
            _l3StreamRenderTimer?.Start();
        }), DispatcherPriority.Background);
    }

    private void L3StreamRenderTimer_Tick(object? sender, EventArgs e)
    {
        if (!_busy || !IsVisible || _activeRequest?.IsCancellationRequested == true)
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
        SetL3ActionState(retry: false, deep: false);
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
