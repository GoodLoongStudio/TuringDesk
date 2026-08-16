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

public sealed record ShellTaskItem(
    string Handle,
    string Title,
    string ShortTitle,
    string ProcessName,
    string? ProcessPath,
    ImageSource? Icon,
    double ActiveOpacity);

public sealed record PinnedShellAppView(
    string Name,
    string Target,
    ImageSource? Icon,
    string Glyph);

public partial class ShellBarWindow : Window
{
    private const string PinnedDragFormat = "TuringDesk.PinnedTarget";
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
    private const int HotkeySwitcher = 0x5103;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkEscape = 0x1B;
    private const uint VkSpace = 0x20;
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
    private readonly TaskSwitcherWindow _taskSwitcher;
    private readonly SessionMenuWindow _sessionMenu;
    private readonly NotificationCenterWindow _notificationCenter;
    private readonly ShellSettingsStore _settingsStore = new();
    private ShellSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _handle;
    private uint _callbackMessage;
    private bool _appBarRegistered;
    private bool _startHotkeyRegistered;
    private bool _desktopHotkeyRegistered;
    private bool _switcherHotkeyRegistered;
    private HwndSource? _source;
    private Point _pinDragStart;
    private string? _pinDragTarget;

    public ObservableCollection<PinnedShellAppView> PinnedApps { get; } = new();
    public ObservableCollection<ShellTaskItem> Tasks { get; } = new();

    public ShellBarWindow(MainWindow controlCenter, DisplayMonitor monitor)
    {
        _controlCenter = controlCenter;
        _monitor = monitor;
        _settings = _settingsStore.Load();
        _startMenu = new StartMenuWindow(controlCenter, monitor);
        _taskSwitcher = new TaskSwitcherWindow(monitor);
        _sessionMenu = new SessionMenuWindow(monitor);
        _notificationCenter = new NotificationCenterWindow(monitor);

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
        ShellSettingsStore.SettingsChanged += OnShellSettingsChanged;
        ShellNotificationService.Changed += OnNotificationsChanged;
        RefreshPinnedApps();
        Refresh();
        _refreshTimer.Start();

        if (_monitor.IsPrimary)
        {
            ShellNotificationService.Publish("TuringDesk Shell 已就绪", "桌面、任务栏、Agent Kernel 与窗口管理已启动。", "shell");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        ShellSettingsStore.SettingsChanged -= OnShellSettingsChanged;
        ShellNotificationService.Changed -= OnNotificationsChanged;
        CloseOwnedPopup(_startMenu);
        CloseOwnedPopup(_taskSwitcher);
        CloseOwnedPopup(_sessionMenu);
        CloseOwnedPopup(_notificationCenter);
        UnregisterShellHotkeys();
        UnregisterAppBar();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private static void CloseOwnedPopup(Window window)
    {
        try
        {
            if (window.IsVisible) window.Hide();
            window.Close();
        }
        catch
        {
            // Shell shutdown should continue even if a popup is already closing.
        }
    }

    private void OnShellSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _settings = _settingsStore.Load();
            RefreshPinnedApps();
        }), DispatcherPriority.Background);
    }

    private void OnNotificationsChanged() => Dispatcher.BeginInvoke(new Action(RefreshNotificationIndicator), DispatcherPriority.Background);

    private void Refresh()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("yyyy/MM/dd");
        RefreshNotificationIndicator();

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
            Tasks.Add(new ShellTaskItem(
                window.Handle,
                window.Title,
                shortTitle,
                window.ProcessName,
                window.ProcessPath,
                icon,
                active ? 1.0 : 0.15));
        }
    }

    private void RefreshNotificationIndicator() =>
        NotificationDot.Visibility = ShellNotificationService.Snapshot().Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshPinnedApps()
    {
        PinnedApps.Clear();
        foreach (var app in _settings.PinnedApps.Take(12))
        {
            var icon = string.IsNullOrWhiteSpace(app.IconTarget)
                ? null
                : ShellIconService.GetIcon(app.IconTarget, large: false);
            PinnedApps.Add(new PinnedShellAppView(app.Name, app.Target, icon, app.Glyph));
        }
    }

    private void SavePins() => _settingsStore.Save(_settings);

    private bool IsPinned(string target) => _settings.PinnedApps.Any(app =>
        string.Equals(app.Target, target, StringComparison.OrdinalIgnoreCase));

    internal void ToggleStartMenu() => _startMenu.Toggle();

    internal void FocusAgent()
    {
        AgentBox.Focus();
        Keyboard.Focus(AgentBox);
    }

    private void Start_Click(object sender, RoutedEventArgs e) => ToggleStartMenu();

    private void Desktop_Click(object sender, RoutedEventArgs e)
    {
        HidePopups();
        _controlCenter.ShowDesktop(true);
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        HidePopups();
        _controlCenter.ShowControlCenter();
    }

    private void TaskSwitcher_Click(object sender, RoutedEventArgs e)
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        if (_sessionMenu.IsVisible) _sessionMenu.Hide();
        if (_notificationCenter.IsVisible) _notificationCenter.Hide();
        _taskSwitcher.Toggle();
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        if (_taskSwitcher.IsVisible) _taskSwitcher.Hide();
        if (_notificationCenter.IsVisible) _notificationCenter.Hide();
        _sessionMenu.Toggle();
    }

    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        if (_taskSwitcher.IsVisible) _taskSwitcher.Hide();
        if (_sessionMenu.IsVisible) _sessionMenu.Hide();
        _notificationCenter.Toggle();
    }

    private void HidePopups()
    {
        if (_startMenu.IsVisible) _startMenu.Hide();
        if (_taskSwitcher.IsVisible) _taskSwitcher.Hide();
        if (_sessionMenu.IsVisible) _sessionMenu.Hide();
        if (_notificationCenter.IsVisible) _notificationCenter.Hide();
    }

    private async void Pinned_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        HidePopups();
        if (target is "chrome" or "code" or "terminal")
        {
            _ = await _launcher.LaunchAsync(target);
        }
        else
        {
            _ = ShellSurfaceCatalog.OpenTarget(target);
        }
    }

    private void Pinned_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        var app = _settings.PinnedApps.FirstOrDefault(candidate =>
            string.Equals(candidate.Target, target, StringComparison.OrdinalIgnoreCase));
        if (app is null) return;

        var menu = new ContextMenu();
        var unpin = new MenuItem { Header = $"从任务栏取消固定 {app.Name}" };
        unpin.Click += (_, _) =>
        {
            _settings.PinnedApps.RemoveAll(candidate =>
                string.Equals(candidate.Target, app.Target, StringComparison.OrdinalIgnoreCase));
            SavePins();
            ShellNotificationService.Publish("已取消固定", app.Name, "shell");
        };
        menu.Items.Add(unpin);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Pinned_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string target }) return;
        _pinDragTarget = target;
        _pinDragStart = e.GetPosition(this);
    }

    private void Pinned_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrWhiteSpace(_pinDragTarget)) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _pinDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var target = _pinDragTarget;
        _pinDragTarget = null;
        var data = new DataObject(PinnedDragFormat, target);
        _ = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void Pinned_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Button { Tag: string dropTarget }) return;
        if (!e.Data.GetDataPresent(PinnedDragFormat) || e.Data.GetData(PinnedDragFormat) is not string sourceTarget) return;
        if (string.Equals(sourceTarget, dropTarget, StringComparison.OrdinalIgnoreCase)) return;

        var source = _settings.PinnedApps.FirstOrDefault(app =>
            string.Equals(app.Target, sourceTarget, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;

        _settings.PinnedApps.Remove(source);
        var targetIndex = _settings.PinnedApps.FindIndex(app =>
            string.Equals(app.Target, dropTarget, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0) _settings.PinnedApps.Add(source);
        else _settings.PinnedApps.Insert(targetIndex, source);

        SavePins();
        ShellNotificationService.Publish("任务栏顺序已更新", source.Name, "shell");
        e.Handled = true;
    }

    private void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string handle })
        {
            _ = _windows.ToggleTask(handle);
            Refresh();
        }
    }

    private void Task_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: ShellTaskItem item } || string.IsNullOrWhiteSpace(item.ProcessPath)) return;

        var alreadyPinned = IsPinned(item.ProcessPath);
        var menu = new ContextMenu();
        var pin = new MenuItem
        {
            Header = alreadyPinned ? "已固定到任务栏" : "固定到任务栏",
            IsEnabled = !alreadyPinned
        };
        pin.Click += (_, _) =>
        {
            if (alreadyPinned || string.IsNullOrWhiteSpace(item.ProcessPath)) return;
            _settings.PinnedApps.Add(new PinnedShellApp(item.ProcessName, item.ProcessPath, item.ProcessPath));
            SavePins();
            ShellNotificationService.Publish("已固定到任务栏", item.ProcessName, "shell");
        };
        menu.Items.Add(pin);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Network_Click(object sender, RoutedEventArgs e) => _ = ShellSurfaceCatalog.OpenTarget("ms-settings:network-status");
    private void Volume_Click(object sender, RoutedEventArgs e) => _ = ShellSurfaceCatalog.OpenTarget("ms-settings:sound");
    private void Power_Click(object sender, RoutedEventArgs e) => Session_Click(sender, e);

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
            if (!string.IsNullOrWhiteSpace(reply))
            {
                var preview = reply.Length <= 140 ? reply : $"{reply[..140]}…";
                ShellNotificationService.Publish("图灵已完成", preview, "agent");
            }
        }
        catch (Exception ex)
        {
            AgentBox.ToolTip = "Agent 请求失败";
            ShellNotificationService.Publish("图灵执行失败", ex.Message, "error");
        }
        finally
        {
            AgentBox.IsEnabled = true;
            AgentBox.Focus();
        }
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

            if (id == HotkeySwitcher)
            {
                _taskSwitcher.Toggle();
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
        _switcherHotkeyRegistered = RegisterHotKey(_handle, HotkeySwitcher, ModControl | ModAlt | ModNoRepeat, VkSpace);
    }

    private void UnregisterShellHotkeys()
    {
        if (_handle == IntPtr.Zero) return;
        if (_startHotkeyRegistered) _ = UnregisterHotKey(_handle, HotkeyStart);
        if (_desktopHotkeyRegistered) _ = UnregisterHotKey(_handle, HotkeyDesktop);
        if (_switcherHotkeyRegistered) _ = UnregisterHotKey(_handle, HotkeySwitcher);
        _startHotkeyRegistered = false;
        _desktopHotkeyRegistered = false;
        _switcherHotkeyRegistered = false;
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
