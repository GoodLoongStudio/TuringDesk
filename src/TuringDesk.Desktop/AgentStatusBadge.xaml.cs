using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class AgentStatusBadge : UserControl
{
    private readonly RuntimeClient _runtime = new();
    private readonly DispatcherTimer _timer;
    private AgentActivityWindow? _activityWindow;
    private bool _refreshing;

    public AgentStatusBadge()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var state = await _runtime.GetAgentStateAsync();
            if (state is null)
            {
                StatusText.Text = "离线";
                StatusDot.Background = BrushFrom("#E07A7A");
                ToolTip = "TuringDesk Runtime 未连接";
                return;
            }

            StatusText.Text = state.Phase switch
            {
                "running" => "执行中",
                "completed" => "已完成",
                "error" => "失败",
                _ => state.Mode.Equals("harness", StringComparison.OrdinalIgnoreCase) ? "图灵" : "Mock"
            };
            StatusDot.Background = state.Phase switch
            {
                "running" => BrushFrom("#7C8CFF"),
                "completed" => BrushFrom("#54D68A"),
                "error" => BrushFrom("#F07D7D"),
                _ => BrushFrom("#7D899D")
            };
            ToolTip = state.Busy && !string.IsNullOrWhiteSpace(state.CurrentPrompt)
                ? $"正在执行：{state.CurrentPrompt}"
                : "打开图灵执行中心";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Status_Click(object sender, RoutedEventArgs e)
    {
        var monitor = DisplayManager.GetPrimary();
        _activityWindow ??= new AgentActivityWindow(monitor);
        _activityWindow.Toggle();
    }

    private static SolidColorBrush BrushFrom(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
