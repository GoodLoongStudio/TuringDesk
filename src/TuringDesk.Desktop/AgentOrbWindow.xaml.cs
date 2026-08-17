using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class AgentOrbWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5444;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    private readonly MainWindow _controlCenter;
    private readonly ShellSettingsStore _settingsStore = new();
    private HwndSource? _source;
    private bool _expanded;
    private bool _primaryHotkeyRegistered;
    private bool _fallbackHotkeyRegistered;
    private bool _orbVisible = true;

    public AgentOrbWindow(MainWindow controlCenter)
    {
        _controlCenter = controlCenter;
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        ShellSettingsStore.SettingsChanged += OnSettingsChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        _primaryHotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModAlt | ModNoRepeat, VkSpace);
        if (_primaryHotkeyRegistered)
        {
            HotkeyHint.Text = "  ALT + SPACE";
            return;
        }

        _fallbackHotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        HotkeyHint.Text = _fallbackHotkeyRegistered ? "  CTRL + ALT + SPACE" : "  CLICK ORB";

        if (!_fallbackHotkeyRegistered)
        {
            ShellNotificationService.Publish(
                "AI 快捷键被占用",
                "Alt+Space 无法注册；仍可点击桌面右下角的 TuringDesk Orb。",
                "warning");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearWorkingArea();
        ApplyOrbPreference();

        if (FindResource("OrbPulse") is Storyboard pulse)
        {
            pulse.Begin(this, true);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ShellSettingsStore.SettingsChanged -= OnSettingsChanged;
        if (FindResource("OrbPulse") is Storyboard pulse)
        {
            try { pulse.Stop(this); } catch { }
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if ((_primaryHotkeyRegistered || _fallbackHotkeyRegistered) && hwnd != IntPtr.Zero)
        {
            _ = UnregisterHotKey(hwnd, HotkeyId);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(ApplyOrbPreference));
    }

    private void ApplyOrbPreference()
    {
        _orbVisible = _settingsStore.Load().Appearance.AgentOrbEnabled;
        if (_expanded) return;

        if (_orbVisible)
        {
            if (!IsVisible) Show();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(ShowComposer));
        }
        return IntPtr.Zero;
    }

    private void Orb_Click(object sender, RoutedEventArgs e)
    {
        if (_expanded) Collapse();
        else ShowComposer();
    }

    private void ShowComposer()
    {
        _expanded = true;
        Width = 456;
        Height = 154;
        ComposerPanel.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
        PositionNearWorkingArea();
        Activate();
        ComposerBox.Focus();
        Keyboard.Focus(ComposerBox);
        ComposerBox.SelectAll();
    }

    private void Collapse()
    {
        _expanded = false;
        ComposerPanel.Visibility = Visibility.Collapsed;
        Width = 76;
        Height = 76;
        PositionNearWorkingArea();

        if (!_orbVisible)
        {
            Hide();
        }
    }

    private void PositionNearWorkingArea()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 22;
    }

    private async Task SendAsync()
    {
        var text = ComposerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        ComposerBox.Clear();
        Collapse();
        await _controlCenter.SubmitExternalCommandAsync(text);
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void ComposerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse();
            return;
        }

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    private void ControlCenter_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        _controlCenter.ShowDiyCenter();
    }

    private void Diy_Click(object sender, RoutedEventArgs e)
    {
        Collapse();
        _controlCenter.ShowDesktopLibrary();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _controlCenter.RequestApplicationExit();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
