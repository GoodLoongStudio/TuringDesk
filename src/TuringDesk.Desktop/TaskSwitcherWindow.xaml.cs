using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed record TaskSwitcherItem(
    string Handle,
    string Title,
    string ProcessName,
    ImageSource? Icon,
    string MonitorLabel);

public partial class TaskSwitcherWindow : Window
{
    private readonly WindowManager _windows = new();
    private readonly DisplayMonitor _monitor;

    public ObservableCollection<TaskSwitcherItem> Tasks { get; } = new();

    public TaskSwitcherWindow(DisplayMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        DataContext = this;
        Deactivated += (_, _) => Hide();
    }

    internal void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        RefreshTasks();
        if (!IsVisible) Show();
        DisplayManager.PositionPopupCenter(this, _monitor);
        Activate();
        if (TasksList.Items.Count > 0)
        {
            TasksList.SelectedIndex = 0;
            TasksList.Focus();
        }
    }

    private void RefreshTasks()
    {
        Tasks.Clear();
        var monitors = DisplayManager.GetMonitors();
        foreach (var window in _windows.ListWindows().Take(48))
        {
            var targetMonitor = monitors.FirstOrDefault(monitor => monitor.ContainsWindowCenter(window));
            var monitorLabel = targetMonitor is null
                ? "窗口"
                : targetMonitor.IsPrimary ? "主屏" : "副屏";
            var icon = string.IsNullOrWhiteSpace(window.ProcessPath)
                ? null
                : ShellIconService.GetIcon(window.ProcessPath, large: false);
            Tasks.Add(new TaskSwitcherItem(window.Handle, window.Title, window.ProcessName, icon, monitorLabel));
        }
    }

    private void ActivateItem(TaskSwitcherItem? item)
    {
        if (item is null) return;
        Hide();
        _ = _windows.Focus(item.Handle);
    }

    private void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string handle })
        {
            ActivateItem(Tasks.FirstOrDefault(item => item.Handle == handle));
        }
    }

    private void TasksList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        ActivateItem(TasksList.SelectedItem as TaskSwitcherItem);

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ActivateItem(TasksList.SelectedItem as TaskSwitcherItem);
        }
    }
}
