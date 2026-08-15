using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed record ShellTaskItem(string Handle, string Title, string ShortTitle);

public partial class ShellBarWindow : Window
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeBottom = 3;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private readonly WindowManager _windows = new();
    private readonly RuntimeClient _runtime = new();
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _handle;
    private uint _callbackMessage;
    private bool _appBarRegistered;

    public ObservableCollection<ShellTaskItem> Tasks { get; } = new();

    public ShellBarWindow()
    {
        InitializeComponent();
        DataContext = this;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        RegisterAppBar();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        UnregisterAppBar();
    }

    private void Refresh()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy/MM/dd");

        var snapshots = _windows.ListWindows()
            .Where(window => !string.Equals(window.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToArray();

        Tasks.Clear();
        foreach (var window in snapshots)
        {
            var shortTitle = window.Title.Length <= 24 ? window.Title : $"{window.Title[..24]}…";
            Tasks.Add(new ShellTaskItem(window.Handle, window.Title, shortTitle));
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var main = Application.Current.MainWindow;
        if (main is null) return;
        main.WindowState = WindowState.Maximized;
        main.Show();
        main.Activate();
    }

    private void Apps_Click(object sender, RoutedEventArgs e) => Home_Click(sender, e);

    private void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string handle })
        {
            _windows.Focus(handle);
        }
    }

    private async void AgentSend_Click(object sender, RoutedEventArgs e) => await SendAgentCommandAsync();

    private async void AgentBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendAgentCommandAsync();
    }

    private async Task SendAgentCommandAsync()
    {
        var text = AgentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        AgentBox.IsEnabled = false;
        try
        {
            AgentBox.Clear();
            var reply = await _runtime.ChatAsync(text);
            AgentBox.ToolTip = string.IsNullOrWhiteSpace(reply)
                ? "Agent 暂时没有返回结果"
                : reply;
        }
        finally
        {
            AgentBox.IsEnabled = true;
            AgentBox.Focus();
        }
    }

    private void RestoreExplorer_Click(object sender, RoutedEventArgs e)
    {
        ShellSession.ExitRequested = true;
        Application.Current.Shutdown(20);
    }

    private void RegisterAppBar()
    {
        if (_handle == IntPtr.Zero || _appBarRegistered) return;

        _callbackMessage = RegisterWindowMessage("TuringDeskShellBarMessage");
        var data = CreateAppBarData();
        data.uCallbackMessage = _callbackMessage;
        _ = SHAppBarMessage(AbmNew, ref data);
        _appBarRegistered = true;

        PositionAppBar();
    }

    private void PositionAppBar()
    {
        if (_handle == IntPtr.Zero) return;

        var dpi = Math.Max(96u, GetDpiForWindow(_handle));
        var scale = dpi / 96d;
        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        var heightPixels = (int)Math.Round(58 * scale);

        var data = CreateAppBarData();
        data.uEdge = AbeBottom;
        data.rc.left = 0;
        data.rc.right = screenWidth;
        data.rc.bottom = screenHeight;
        data.rc.top = screenHeight - heightPixels;

        _ = SHAppBarMessage(AbmQueryPos, ref data);
        data.rc.top = data.rc.bottom - heightPixels;
        _ = SHAppBarMessage(AbmSetPos, ref data);

        Left = data.rc.left / scale;
        Top = data.rc.top / scale;
        Width = Math.Max(1, (data.rc.right - data.rc.left) / scale);
        Height = Math.Max(1, (data.rc.bottom - data.rc.top) / scale);
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered || _handle == IntPtr.Zero) return;
        var data = CreateAppBarData();
        _ = SHAppBarMessage(AbmRemove, ref data);
        _appBarRegistered = false;
    }

    private AppBarData CreateAppBarData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _handle
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
