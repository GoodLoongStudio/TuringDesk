using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed record ShellNotificationView(string Title, string Message, string Kind, string IconKind, string TimeText);

public partial class NotificationCenterWindow : Window
{
    private readonly DisplayMonitor _monitor;
    public ObservableCollection<ShellNotificationView> Notifications { get; } = new();

    public NotificationCenterWindow(DisplayMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => ShellNotificationService.Changed -= OnNotificationsChanged;
        Deactivated += (_, _) => Hide();
        ShellNotificationService.Changed += OnNotificationsChanged;
    }

    internal void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Refresh();
        if (!IsVisible) Show();
        PositionWindow();
        Activate();
        Focus();
    }

    private void PositionWindow()
    {
        var current = DisplayManager.GetMonitors().FirstOrDefault(monitor => monitor.Id == _monitor.Id) ?? _monitor;
        DisplayManager.PositionPopupBottomRight(this, current, marginPixels: 10);
    }

    private void OnNotificationsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        Refresh();
    }

    private static string ResolveIconKind(string kind)
    {
        var value = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Contains("agent")) return "Agent";
        if (value.Contains("error") || value.Contains("fail")) return "Error";
        if (value.Contains("warn")) return "Warning";
        if (value.Contains("success") || value.Contains("done")) return "Success";
        if (value.Contains("file") || value.Contains("desktop")) return "File";
        if (value.Contains("shell")) return "Desktop";
        return "Info";
    }

    private void Refresh()
    {
        var items = ShellNotificationService.Snapshot();
        Notifications.Clear();
        foreach (var item in items)
        {
            Notifications.Add(new ShellNotificationView(
                item.Title,
                item.Message,
                item.Kind,
                ResolveIconKind(item.Kind),
                item.Timestamp.LocalDateTime.ToString("HH:mm")));
        }
        CountText.Text = $"{Notifications.Count} 条通知";
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ShellNotificationService.Clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Hide();
    }
}
