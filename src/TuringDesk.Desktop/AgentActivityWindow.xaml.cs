using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed record AgentHistoryView(
    string Prompt,
    string Detail,
    string Time,
    Brush StatusBrush);

public partial class AgentActivityWindow : Window
{
    private readonly DisplayMonitor _monitor;
    private readonly RuntimeClient _runtime = new();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    public ObservableCollection<AgentHistoryView> History { get; } = new();

    public AgentActivityWindow(DisplayMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        DataContext = this;
        Deactivated += (_, _) => Hide();
        Closed += (_, _) => _timer.Stop();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    internal void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            _timer.Stop();
            return;
        }

        if (!IsVisible) Show();
        DisplayManager.PositionPopupBottomRight(this, _monitor, 10);
        Activate();
        _timer.Start();
        _ = RefreshAsync();
    }

    internal async Task<AgentActivityState?> RefreshAsync()
    {
        if (_refreshing) return null;
        _refreshing = true;
        try
        {
            var state = await _runtime.GetAgentStateAsync();
            ApplyState(state);
            return state;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyState(AgentActivityState? state)
    {
        if (state is null)
        {
            KernelText.Text = "Runtime 未连接";
            StatusText.Text = "Runtime 离线";
            StatusDot.Background = BrushFrom("#E07A7A");
            CurrentPromptText.Text = "TuringDesk Runtime 暂时不可用。";
            ElapsedText.Text = string.Empty;
            ResultCard.Visibility = Visibility.Collapsed;
            History.Clear();
            return;
        }

        KernelText.Text = state.Mode.Equals("harness", StringComparison.OrdinalIgnoreCase)
            ? "DeepSeek Harness · Agent Kernel"
            : "TuringDesk Mock Runtime";

        StatusText.Text = state.Phase switch
        {
            "running" => "图灵正在执行",
            "completed" => "最近任务已完成",
            "error" => "最近任务执行失败",
            _ => "图灵空闲"
        };
        StatusDot.Background = state.Phase switch
        {
            "running" => BrushFrom("#7C8CFF"),
            "completed" => BrushFrom("#54D68A"),
            "error" => BrushFrom("#F07D7D"),
            _ => BrushFrom("#7D899D")
        };

        CurrentPromptText.Text = state.Busy && !string.IsNullOrWhiteSpace(state.CurrentPrompt)
            ? state.CurrentPrompt
            : "当前没有执行中的桌面任务";

        if (state.Busy && state.StartedAt is not null)
        {
            var elapsed = DateTimeOffset.Now - state.StartedAt.Value;
            ElapsedText.Text = $"Run #{state.RunId} · 已运行 {Math.Max(0, (int)elapsed.TotalSeconds)} 秒";
        }
        else
        {
            ElapsedText.Text = state.RunId > 0 ? $"最近 Run #{state.RunId}" : string.Empty;
        }

        var result = state.Error ?? state.ReplyPreview;
        ResultCard.Visibility = string.IsNullOrWhiteSpace(result) ? Visibility.Collapsed : Visibility.Visible;
        ResultTitle.Text = state.Error is null ? "最近结果" : "错误";
        ResultText.Text = result ?? string.Empty;
        ResultText.Foreground = state.Error is null ? BrushFrom("#CED5E3") : BrushFrom("#F0A0A0");

        History.Clear();
        foreach (var item in state.History.Take(8))
        {
            var detail = item.Error ?? item.ReplyPreview ?? item.Phase;
            var brush = item.Phase switch
            {
                "completed" => BrushFrom("#54D68A"),
                "error" => BrushFrom("#F07D7D"),
                "running" => BrushFrom("#7C8CFF"),
                _ => BrushFrom("#7D899D")
            };
            History.Add(new AgentHistoryView(
                item.Prompt,
                string.IsNullOrWhiteSpace(detail) ? item.Phase : detail,
                item.StartedAt.ToLocalTime().ToString("HH:mm"),
                brush));
        }
    }

    private static SolidColorBrush BrushFrom(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Hide();
    }
}
