using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class AgentTraceCardWindow : Window
{
    public AgentTraceCardWindow()
    {
        InitializeComponent();
    }

    internal void SetState(AgentActivityState? state)
    {
        if (state is null)
        {
            KernelText.Text = "Runtime offline";
            PhaseText.Text = "无法读取执行状态";
            ElapsedText.Text = string.Empty;
            return;
        }

        KernelText.Text = state.Mode.Equals("harness", StringComparison.OrdinalIgnoreCase)
            ? "DeepSeek Harness · Agent Kernel"
            : "TuringDesk Runtime";
        PhaseText.Text = state.Phase switch
        {
            "running" => "正在执行桌面任务",
            "completed" => "执行完成",
            "error" => "执行失败",
            _ => "Agent 空闲"
        };

        if (state.StartedAt is not null)
        {
            var end = state.FinishedAt ?? DateTimeOffset.Now;
            ElapsedText.Text = $"Run #{state.RunId} · {Math.Max(0, (int)(end - state.StartedAt.Value).TotalSeconds)}s";
        }
        else
        {
            ElapsedText.Text = state.RunId > 0 ? $"Run #{state.RunId}" : string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in state.Trace.TakeLast(24))
        {
            var local = item.At.ToLocalTime();
            builder.Append(local.ToString("HH:mm:ss"));
            builder.Append("  ");
            builder.Append('[').Append(item.Kind).Append("]  ");
            builder.AppendLine(item.Text);
        }

        if (builder.Length == 0)
        {
            builder.AppendLine(state.Busy ? "等待 DeepSeek Harness 事件…" : "暂无执行轨迹。");
        }

        var keepSelection = TraceBox.IsKeyboardFocusWithin;
        if (!keepSelection || !string.Equals(TraceBox.Text, builder.ToString(), StringComparison.Ordinal))
        {
            var selectionStart = TraceBox.SelectionStart;
            var selectionLength = TraceBox.SelectionLength;
            TraceBox.Text = builder.ToString();
            if (keepSelection)
            {
                TraceBox.SelectionStart = Math.Min(selectionStart, TraceBox.Text.Length);
                TraceBox.SelectionLength = Math.Min(selectionLength, Math.Max(0, TraceBox.Text.Length - TraceBox.SelectionStart));
            }
            else
            {
                TraceBox.ScrollToEnd();
            }
        }
    }

    internal void ShowAnimated(double targetOpacity)
    {
        if (!IsVisible) Show();
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Clamp(targetOpacity, 0.7, 1.0), TimeSpan.FromMilliseconds(145))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    internal void ApplyOpacity(double value)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = Math.Clamp(value, 0.7, 1.0);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TraceBox.Text)) Clipboard.SetText(TraceBox.Text);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) return;
        TraceBox.SelectedText = Clipboard.GetText();
        TraceBox.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}