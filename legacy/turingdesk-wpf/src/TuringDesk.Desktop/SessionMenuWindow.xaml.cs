using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class SessionMenuWindow : Window
{
    private const uint EwxLogoff = 0x00000000;
    private readonly DisplayMonitor _monitor;

    public SessionMenuWindow(DisplayMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        Deactivated += (_, _) => Hide();
    }

    internal void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        if (!IsVisible) Show();
        DisplayManager.PositionPopupBottomRight(this, _monitor);
        Activate();
        Focus();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _ = LockWorkStation();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("注销当前 Windows 用户？")) return;
        Hide();
        _ = ExitWindowsEx(EwxLogoff, 0);
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("立即重新启动 Windows？")) return;
        Hide();
        StartShutdown("/r /t 0");
    }

    private void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("立即关闭 Windows？")) return;
        Hide();
        StartShutdown("/s /t 0");
    }

    private void RestoreExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("退出 TuringDesk Shell 并恢复 Explorer？")) return;
        Hide();
        ShellSession.ExitRequested = true;
        Application.Current.Shutdown(20);
    }

    private static bool Confirm(string message) =>
        MessageBox.Show(message, "TuringDesk", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void StartShutdown(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            MessageBox.Show("无法调用 Windows 电源操作。", "TuringDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
}
