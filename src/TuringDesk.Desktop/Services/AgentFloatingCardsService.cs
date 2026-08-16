using System.Windows;
using System.Windows.Threading;

namespace TuringDesk.Desktop.Services;

public static class AgentFloatingCardsService
{
    private static readonly RuntimeClient Runtime = new();
    private static readonly ShellSettingsStore SettingsStore = new();
    private static AgentConversationCardWindow? _conversation;
    private static AgentTraceCardWindow? _trace;
    private static DispatcherTimer? _pollTimer;
    private static DisplayMonitor? _monitor;
    private static int _generation;

    static AgentFloatingCardsService()
    {
        ShellSettingsStore.SettingsChanged += OnSettingsChanged;
    }

    public static void Begin(DisplayMonitor monitor, string prompt)
    {
        var settings = SettingsStore.Load().Appearance;
        if (!settings.AgentCardsEnabled) return;

        EnsureWindows();
        _generation++;
        _monitor = monitor;
        _conversation!.SetPrompt(prompt);
        _trace!.SetState(null);
        ApplySettings(settings);
        Position(settings);
        _conversation.ShowAnimated(settings.AgentCardOpacity);
        _trace.ShowAnimated(settings.AgentCardOpacity);
        StartPolling();
        _ = RefreshAsync(_generation);
    }

    public static void Complete(string? reply)
    {
        if (_conversation is null) return;
        if (!string.IsNullOrWhiteSpace(reply)) _conversation.SetReply(reply);
        _ = RefreshAsync(_generation);
        ScheduleAutoHide(_generation);
    }

    public static void Fail(string message)
    {
        if (_conversation is null) return;
        _conversation.SetReply(message, failed: true);
        _ = RefreshAsync(_generation);
        ScheduleAutoHide(_generation);
    }

    public static void Hide()
    {
        _pollTimer?.Stop();
        _conversation?.Hide();
        _trace?.Hide();
    }

    private static void EnsureWindows()
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            _conversation ??= new AgentConversationCardWindow();
            _trace ??= new AgentTraceCardWindow();
            return;
        }

        Application.Current.Dispatcher.Invoke(EnsureWindows);
    }

    private static void StartPolling()
    {
        _pollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        _pollTimer.Tick -= PollTimer_Tick;
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
    }

    private static async void PollTimer_Tick(object? sender, EventArgs e) => await RefreshAsync(_generation);

    private static async Task RefreshAsync(int generation)
    {
        var state = await Runtime.GetAgentStateAsync();
        if (generation != _generation || _conversation is null || _trace is null) return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (generation != _generation) return;
            _trace.SetState(state);
            if (state is not null)
            {
                _conversation.SetState(state.RunId, state.Phase, state.ReplyPreview, state.Error);
                if (!state.Busy && state.Phase is "completed" or "error")
                {
                    _pollTimer?.Stop();
                }
            }
        });
    }

    private static void ScheduleAutoHide(int generation)
    {
        var seconds = SettingsStore.Load().Appearance.AgentCardAutoHideSeconds;
        if (seconds <= 0) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (generation != _generation) return;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (generation == _generation) Hide();
            });
        });
    }

    private static void OnSettingsChanged()
    {
        if (_conversation is null || _trace is null) return;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var settings = SettingsStore.Load().Appearance;
            if (!settings.AgentCardsEnabled)
            {
                Hide();
                return;
            }
            ApplySettings(settings);
            Position(settings);
        }), DispatcherPriority.Background);
    }

    private static void ApplySettings(ShellAppearanceSettings settings)
    {
        _conversation?.ApplyOpacity(settings.AgentCardOpacity);
        _trace?.ApplyOpacity(settings.AgentCardOpacity);
    }

    private static void Position(ShellAppearanceSettings settings)
    {
        if (_monitor is null || _conversation is null || _trace is null) return;

        const int gap = 12;
        var conversationWidthPixels = 410 + gap;
        if (string.Equals(settings.AgentCardSide, "left", StringComparison.OrdinalIgnoreCase))
        {
            DisplayManager.PositionAgentCard(_conversation, _monitor, "left", 0);
            DisplayManager.PositionAgentCard(_trace, _monitor, "left", conversationWidthPixels);
        }
        else
        {
            DisplayManager.PositionAgentCard(_conversation, _monitor, "right", 0);
            DisplayManager.PositionAgentCard(_trace, _monitor, "right", conversationWidthPixels);
        }
    }
}