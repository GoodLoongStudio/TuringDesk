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
    private bool _l3StreamingInitialized;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // InitializeComponent can raise Initialized before the constructor has created
        // _quickAnswer and the dynamically-added L3 action buttons. Defer all L3
        // subscriptions until Loaded, when the window's constructor is complete.
        Loaded += L3Streaming_Loaded;
        Closed += L3Streaming_Closed;
    }

    private void L3Streaming_Loaded(object sender, RoutedEventArgs e)
    {
        if (_l3StreamingInitialized) return;
        _l3StreamingInitialized = true;
        Loaded -= L3Streaming_Loaded;

        _l3StreamRenderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _l3StreamRenderTimer.Tick += L3StreamRenderTimer_Tick;

        _quickAnswer.PartialResponseUpdated += OnL3PartialResponseUpdated;
        PreviewKeyDown += L3Window_PreviewKeyDown;
        if (_retryButton is not null)
            _retryButton.Click += L3RetryButton_Click;
    }

    private void L3Streaming_Closed(object? sender, EventArgs e)
    {
        Loaded -= L3Streaming_Loaded;

        if (_l3StreamingInitialized)
        {
            _quickAnswer.PartialResponseUpdated -= OnL3PartialResponseUpdated;
            PreviewKeyDown -= L3Window_PreviewKeyDown;
            if (_retryButton is not null)
                _retryButton.Click -= L3RetryButton_Click;
        }

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
        // Retry_Click is registered first and marks the request busy before its first
        // await, so this companion handler can safely attach the watchdog to retries.
        if (!_busy) return;
        StartL3TimeoutWatchdog(SearchBox.Text.Trim());
    }

    private void StartL3TimeoutWatchdog(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        // A completed/cancelled request can leave its final coalesced partial cached.
        // Never let that state suppress an identical prefix from the next request or
        // briefly repaint stale text when a retry/new turn starts.
        _l3StreamRenderTimer?.Stop();
        lock (_l3StreamGate)
        {
            _pendingL3Partial = null;
            _renderedL3Partial = null;
        }
        Interlocked.Exchange(ref _l3StreamStartScheduled, 0);

        _l3UserCancellationRequested = false;
        var generation = Interlocked.Increment(ref _l3TimeoutWatchdogGeneration);
        _ = WatchL3TimeoutAsync(prompt, generation);
    }

    private async Task WatchL3TimeoutAsync(string prompt, long generation)
    {
        await Task.Delay(SearchResponseTimeout + TimeSpan.FromMilliseconds(250));
        if (!IsVisible || generation != Volatile.Read(ref _l3TimeoutWatchdogGeneration)) return;
        if (_l3UserCancellationRequested || !string.Equals(SearchBox.Text.Trim(), prompt, StringComparison.Ordinal)) return;

        // A request can finish normally long before this delayed watchdog wakes up.
        // The successful/requires-model/deep-processing result remains rendered even
        // though _busy has already been released; never replace that completed result
        // with a stale timeout just because the input text itself has not changed.
        if (!_busy && !string.IsNullOrWhiteSpace(ReplyText.Text)) return;

        // The request owns its timeout CTS and unwinds through the same cancellation
        // catch as Escape. If we paint the final timeout error while the provider is
        // still unwinding, that catch can restore idle state afterwards and erase the
        // retry affordance. Cancel now, keep the request serialized, and publish the
        // actionable timeout only after the provider has actually released _busy.
        _activeRequest?.Cancel();
        if (_busy)
        {
            ReplyTitle.Text = "CLI 响应超时 · 正在停止";
            ReplyText.Text = "回答已超过时限，正在安全停止当前请求…";
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(225, 91, 91));
            SetL3ActionState(retry: false, deep: false);
            _l3StreamRenderTimer?.Stop();
            ExpandReply(150);
        }

        while (_busy &&
               IsVisible &&
               generation == Volatile.Read(ref _l3TimeoutWatchdogGeneration) &&
               !_l3UserCancellationRequested)
        {
            await Task.Delay(50);
        }

        if (!IsVisible || generation != Volatile.Read(ref _l3TimeoutWatchdogGeneration)) return;
        if (_l3UserCancellationRequested || !string.Equals(SearchBox.Text.Trim(), prompt, StringComparison.Ordinal)) return;

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