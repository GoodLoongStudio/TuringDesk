using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed record ShellTaskItem(string Handle, string Title, string ShortTitle, ImageSource? Icon, double ActiveOpacity);

public partial class ShellBarWindow : Window
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbeBottom = 3;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmHotkey = 0x0312;
    private const int WmDpiChanged = 0x02E0;
    private const int HotkeyStart = 0x5101;
    private const int HotkeyDesktop = 0x5102;
    private const uint ModControl = 0x0002;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkEscape = 0x1B;
    private const uint VkD = 0x44;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly MainWindow _controlCenter;
    private readonly DisplayMonitor _monitor;
    private readonly WindowManager _windows = new();
    private readonly RuntimeClient _runtime = new();
    private readonly AppLauncher _launcher = new();
    private readonly StartMenuWindow _startMenu;
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _handle;
    private uint _callbackMessage;
    private bool _appBarRegistered;
    private bool _startHotkeyRegistered;
    private bool _desktopHotkeyRegistered;
    private HwndSource? _source;

    public ObservableCollection<ShellTaskItem> Tasks { get; } = new();

    public ShellBarWindow(MainWindow controlCenter, DisplayMonitor monitor)
    {
        _controlCenter = controlCenter;
        _monitor = monitor;
        _startMenu = new StartMenuWindow(controlCenter, monitor);

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
        RegisterShellHotkeys();
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
        UnregisterShellHotkeys();
        UnregisterAppBar();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void Refresh()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy/MM/dd");

        var status = SystemStatusService.Read();
        NetworkGlyph.Text = status.NetworkAvailable ? "●" : "○";
        NetworkGlyph.Foreground = new SolidColorBrush(status.NetworkAvailable ? Color.FromRgb(84, 214, 138) : Color.FromRgb(240, 125, 125));
        NetworkButton.ToolTip = status.NetworkLabel;
        PowerText.Text = status.HasBattery && status.BatteryPercent is not null ? $"{status.BatteryPercent}%" : "AC";
        PowerButton.ToolTip = status.BatteryLabel;

        var activeHandle = _windows.GetForegroundHandle();
        var snapshots = _windows.ListWindows()
            .Where(_monitor.ContainsWindowCenter)
            .Take(16)
            .ToArray();

        Tasks.Clear();
        foreach (var window in snapshots)
        {
            var shortTitle = window.Title.Length <= 22 ? window.Title : $"{window.Title[..22]}…";
            var active = string.Equals(activeHandle, window.Handle, StringComparison.Ordinal);
            var icon = string.IsNullOrWhiteSpace(window.ProcessPath) ? null : ShellIconService.GetIcon(window.ProcessPath, large: false);
            Tasks.Add(new ShellTaskItem(window.Handle, window.Title, shortTitle, icon, active ? 1.0 : 0.15));
        }
    }

    internal void ToggleStartMenu() => _startMenu.Toggle();

    internal void FocusAgent()
    {
        AgentBox.Focus();
        Keyboard.Focus(AgentBox);
    }

    private void Start_Click(object sender, RoutedEventArgs e) => ToggleStartMenu();

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

    private void Network_Click(object sender, RoutedEventArgs e) => _ = ShellSurfaceCatalog.OpenTarget("ms-settings:network-status");
    private void Volume_Click(object sender, RoutedEventArgs e) => _ = ShellSurfaceCatalog.OpenTarget("ms-settings:sound");
    private void Power_Click(object sender, RoutedEventArgs e) => _ = ShellSurfaceCatalog.OpenTarget("ms-settings:powersleep");

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
            AgentBox.ToolTip = string.IsNullOrWhiteSpace(reply) ? "Agent 暂时没有返回结果" : reply;
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
        if (msg == WmHotkey && _monitor.IsPrimary)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyStart)
            {
                ToggleStartMenu();
                handled = true;
                return IntPtr.Zero;
            }

            if (id == HotkeyDesktop)
            {
                _controlCenter.ShowDesktop(true);
                handled = true;
                return IntPtr.Zero;
            }
        }

        if ((uint)msg == _callbackMessage || msg is WmSettingChange or WmDisplayChange or WmDpiChanged)
        {
            Dispatcher.BeginInvoke(new Action(PositionAppBar), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void RegisterShellHotkeys()
    {
        if (!_monitor.IsPrimary || _handle == IntPtr.Zero) return;
        _startHotkeyRegistered = RegisterHotKey(_handle, HotkeyStart, ModControl | ModNoRepeat, VkEscape);
        _desktopHotkeyRegistered = RegisterHotKey(_handle, HotkeyDesktop, ModWin | ModNoRepeat, VkD);
    }

    private void UnregisterShellHotkeys()
    {
        if (_handle == IntPtr.Zero) return;
        if (_startHotkeyRegistered) _ = UnregisterHotKey(_handle, HotkeyStart);
        if (_desktopHotkeyRegistered) _ = UnregisterHotKey(_handle, HotkeyDesktop);
        _startHotkeyRegistered = false;
        _desktopHotkeyRegistered = false;
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
        var heightPixels = (int)Math.Round(60 * scale);

        var data = CreateAppBarData();
        data.uEdge = AbeBottom;
        data.rc.left = _monitor.Left;
        data.rc.right = _monitor.Right;
        data.rc.bottom = _monitor.Bottom;
        data.rc.top = _monitor.Bottom - heightPixels;

        _ = SHAppBarMessage(AbmQueryPos, ref data);
        data.rc.top = data.rc.bottom - heightPixels;
        _ = SHAppBarMessage(AbmSetPos, ref data);

        _ = SetWindowPos(
            _handle,
            HwndTopmost,
            data.rc.left,
            data.rc.top,
            Math.Max(1, data.rc.right - data.rc.left),
            Math.Max(1, data.rc.bottom - data.rc.top),
            SwpNoActivate | SwpShowWindow);
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered || _handle == IntPtr.Zero) return;
        var data = CreateAppBarData();
        _ = SHAppBarMessage(AbmRemove, ref data);
        _appBarRegistered = false;
    }

    private AppBarData CreateAppBarData() => new() { cbSize = (uint)Marshal.SizeOf<AppBarData>(), hWnd = _handle };

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int left; public int top; public int right; public int bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AppBarData { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public NativeRect rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
