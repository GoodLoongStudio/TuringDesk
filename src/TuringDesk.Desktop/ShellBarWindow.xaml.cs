using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed record ShellTaskItem(string Handle, string Title, string ShortTitle, double ActiveOpacity);

public partial class ShellBarWindow : Window
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeBottom = 3;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;

    private readonly MainWindow _controlCenter;
    private readonly WindowManager _windows = new();
    private readonly RuntimeClient _runtime = new();
    private readonly AppLauncher _launcher = new();
    private readonly StartMenuWindow _startMenu;
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _handle;
    private uint _callbackMessage;
    private bool _appBarRegistered;
    private HwndSource? _source;

    public ObservableCollection<ShellTaskItem> Tasks { get; } = new();

    public ShellBarWindow(MainWindow controlCenter)
    {
        _controlCenter = controlCenter;
        _startMenu = new StartMenuWindow(controlCenter);

        InitializeComponent();
        DataContext = this;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
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
        if (_startMenu.IsVisible) _startMenu.Hide();
        _startMenu.Close();
        UnregisterAppBar();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void Refresh()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy/MM/dd");

        var activeHandle = _windows.GetForegroundHandle();
        var snapshots = _windows.ListWindows().Take(16).ToArray();

        Tasks.Clear();
        foreach (var window in snapshots)
        {
            var shortTitle = window.Title.Length <= 24 ? window.Title : $"{window.Title[..24]}…";
            var active = string.Equals(activeHandle, window.Handle, StringComparison.Ordinal);
            Tasks.Add(new ShellTaskItem(window.Handle, window.Title, shortTitle, active ? 1.0 : 0.15));
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _startMenu.Toggle();

    private void Desktop_Click(object sender, RoutedEventArgs e)
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        _controlCenter.ShowDesktop(true);
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        _controlCenter.ShowControlCenter();
    }

    private async Task LaunchPinnedAsync(string app)
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        _ = await _launcher.LaunchAsync(app);
    }

    private async void Chrome_Click(object sender, RoutedEventArgs e) => await LaunchPinnedAsync("chrome");
    private async void VSCode_Click(object sender, RoutedEventArgs e) => await LaunchPinnedAsync("code");
    private async void Terminal_Click(object sender, RoutedEventArgs e) => await LaunchPinnedAsync("terminal");

    private void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string handle })
        {
            _ = _windows.ToggleTask(handle);
            Refresh();
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
        if (_startMenu.IsVisible) _startMenu.Hide();
        ShellSession.ExitRequested = true;
        Application.Current.Shutdown(20);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == _callbackMessage || msg is WmSettingChange or WmDisplayChange or WmDpiChanged)
        {
            Dispatcher.BeginInvoke(new Action(PositionAppBar), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
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
        var heightPixels = (int)Math.Round(60 * scale);

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
