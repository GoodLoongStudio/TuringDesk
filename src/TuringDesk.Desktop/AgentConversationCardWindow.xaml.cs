using System.Windows;
using System.Windows.Media.Animation;

namespace TuringDesk.Desktop;

public partial class AgentConversationCardWindow : Window
{
    public AgentConversationCardWindow()
    {
        InitializeComponent();
    }

    internal void SetPrompt(string prompt)
    {
        PromptBox.Text = prompt;
        ReplyBox.Text = "图灵正在处理…";
        StatusText.Text = "正在执行";
        RunText.Text = "Run --";
    }

    internal void SetState(int runId, string phase, string? reply, string? error)
    {
        RunText.Text = runId > 0 ? $"Run #{runId}" : "Run --";
        StatusText.Text = phase switch
        {
            "running" => "正在执行",
            "completed" => "执行完成",
            "error" => "执行失败",
            _ => "等待中"
        };

        if (!string.IsNullOrWhiteSpace(error)) ReplyBox.Text = error;
        else if (!string.IsNullOrWhiteSpace(reply)) ReplyBox.Text = reply;
    }

    internal void SetReply(string reply, bool failed = false)
    {
        ReplyBox.Text = reply;
        StatusText.Text = failed ? "执行失败" : "执行完成";
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

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var content = $"你的请求\n{PromptBox.Text}\n\n图灵回复\n{ReplyBox.Text}";
        if (!string.IsNullOrWhiteSpace(content)) Clipboard.SetText(content);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) return;
        var target = Keyboard.FocusedElement == PromptBox ? PromptBox : ReplyBox;
        target.SelectedText = Clipboard.GetText();
        target.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}