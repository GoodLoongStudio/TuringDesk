using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5444;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    private readonly MainWindow _host;
    private HwndSource? _source;
    private bool _hotkeyRegistered;
    private bool _fallbackHotkeyRegistered;
    private bool _busy;

    public DesktopSearchBarWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            RefreshPosition();
            DesktopSearchReservedArea.Publish(this);
        };
        SizeChanged += (_, _) =>
        {
            RefreshPosition();
            DesktopSearchReservedArea.Publish(this);
        };
        LocationChanged += (_, _) => DesktopSearchReservedArea.Publish(this);

        // A WPF top-level window is raised when it receives activation. Re-pin
        // after the activation transaction so clicking/focusing the desktop
        // search never turns it into an application-level overlay.
        Activated += (_, _) => Dispatcher.BeginInvoke(
            new Action(RefreshPosition),
            DispatcherPriority.ContextIdle);
        Deactivated += (_, _) => RefreshPosition();
        Closed += OnClosed;
    }

    internal void RefreshPosition()
    {
        if (!IsInitialized) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = DisplayManager.GetPrimary();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY));
        const int marginPixels = 10;
        const int topOffsetPixels = 38;

        var x = monitor.WorkLeft + (monitor.WorkWidth - width) / 2;
        var y = monitor.WorkTop + Math.Max(marginPixels, topOffsetPixels);
        x = Math.Max(monitor.WorkLeft + marginPixels, Math.Min(x, monitor.WorkRight - width - marginPixels));
        y = Math.Max(monitor.WorkTop + marginPixels, Math.Min(y, monitor.WorkBottom - height - marginPixels));

        _ = DesktopWidgetZOrderService.PositionAboveExplorerDesktop(hwnd, x, y, width, height);
    }

    internal void FocusSearch()
    {
        if (!IsVisible) Show();

        // Activate only long enough to give the TextBox keyboard focus, then
        // immediately return the HWND to the desktop Z-order band. This keeps
        // Alt+Space useful without leaving the bar above normal applications.
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
        RefreshPosition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        // Place the HWND in the desktop band before the first WPF paint.
        RefreshPosition();

        _hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModAlt | ModNoRepeat, VkSpace);
        if (!_hotkeyRegistered)
        {
            _fallbackHotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
            if (!_fallbackHotkeyRegistered)
            {
                ShellNotificationService.Publish(
                    "AI 快捷键被占用",
                    "Alt+Space 和 Ctrl+Alt+Space 都被其他程序占用；顶部搜索框仍可直接点击使用。",
                    "warning");
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DesktopSearchReservedArea.Clear();
        var hwnd = new WindowInteropHelper(this).Handle;
        if ((_hotkeyRegistered || _fallbackHotkeyRegistered) && hwnd != IntPtr.Zero)
        {
            _ = UnregisterHotKey(hwnd, HotkeyId);
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(FocusSearch));
        }
        return IntPtr.Zero;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CollapseReply();
            SearchBox.Clear();
            Keyboard.ClearFocus();
            return;
        }

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SubmitAsync();
        }
    }

    private async Task SubmitAsync()
    {
        var prompt = SearchBox.Text.Trim();
        if (_busy || string.IsNullOrWhiteSpace(prompt)) return;

        _busy = true;
        SearchBox.IsEnabled = false;
        ReplyTitle.Text = "图灵 · 正在处理";
        ReplyText.Text = prompt;
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(127, 143, 255));
        ExpandReply();

        try
        {
            var reply = await _host.SubmitSearchCommandAsync(prompt);
            ReplyTitle.Text = "图灵";
            ReplyText.Text = string.IsNullOrWhiteSpace(reply) ? "没有返回内容。" : reply;
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(99, 230, 190));
            SearchBox.Clear();
        }
        catch (Exception error)
        {
            ReplyTitle.Text = "图灵 · 未完成";
            ReplyText.Text = error.Message;
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(240, 125, 125));
        }
        finally
        {
            _busy = false;
            SearchBox.IsEnabled = true;
            SearchBox.Focus();
            RefreshPosition();
        }
    }

    private void ExpandReply()
    {
        ReplyPanel.Visibility = Visibility.Visible;
        ReplyRow.Height = new GridLength(150);
        Height = 236;
        RefreshPosition();
    }

    private void CollapseReply()
    {
        ReplyPanel.Visibility = Visibility.Collapsed;
        ReplyRow.Height = new GridLength(0);
        Height = 78;
        RefreshPosition();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _host.ShowDesktopLibrary();
        RefreshPosition();
    }

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        FocusSearch();
        ShellNotificationService.Publish(
            "语音已常驻",
            "直接说“图灵桌面”再说你的需求；识别结果会交给同一个 AI Runtime。",
            "voice");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
