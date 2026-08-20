using System.Windows;
using System.Windows.Media;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _quickAnswer.PartialResponseUpdated += OnL3PartialResponseUpdated;
        Closed += (_, _) => _quickAnswer.PartialResponseUpdated -= OnL3PartialResponseUpdated;
    }

    private void OnL3PartialResponseUpdated(string partial)
    {
        if (string.IsNullOrEmpty(partial)) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_busy || !IsVisible) return;

            ReplyTitle.Text = "CLI · 回答中";
            ReplyText.Text = partial;
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(127, 143, 255));
            DeepProcessButton.Visibility = Visibility.Collapsed;
            CollapseSearchResults();
            ExpandReply(CalculateStreamingReplyHeight(partial));
        }));
    }

    private static double CalculateStreamingReplyHeight(string text)
    {
        if (text.Length <= 260) return 150;
        if (text.Length <= 900) return 190;
        return 230;
    }
}
