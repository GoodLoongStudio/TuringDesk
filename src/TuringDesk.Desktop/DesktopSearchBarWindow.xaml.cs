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
    private const double CompactHeight = 78;
    private static readonly TimeSpan SearchResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly MainWindow _host;
    private readonly DesktopSearchIndexService _searchIndex;
    private HwndSource? _source;
    private bool _hotkeyRegistered;
    private bool _fallbackHotkeyRegistered;
    private bool _busy;
    private bool _modelsInitializing;
    private CancellationTokenSource? _activeRequest;
    private CancellationTokenSource? _searchDebounce;
    private DesktopAiModelChoice? _selectedModel;

    public DesktopSearchBarWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        _searchIndex = new DesktopSearchIndexService();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            RefreshModelChoices();
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
        _activeRequest?.Cancel();
        _activeRequest?.Dispose();
        _activeRequest = null;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        _searchIndex.Dispose();

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

    private void RefreshModelChoices()
    {
        _modelsInitializing = true;
        try
        {
            var choices = _host.LoadDesktopSearchModelChoices();
            ModelSelector.ItemsSource = choices;
            _selectedModel = _host.ResolveDesktopSearchDefaultModel(choices);
            ModelSelector.SelectedItem = _selectedModel;
            UpdateModelSelectorHint();
        }
        finally
        {
            _modelsInitializing = false;
        }
    }

    private void UpdateModelSelectorHint()
    {
        if (_selectedModel is null)
        {
            ModelSelector.ToolTip = "没有可用 AI 模型；点击配置。";
            return;
        }

        ModelSelector.ToolTip = _selectedModel.IsAvailable
            ? $"AI 模型：{_selectedModel.DisplayName}\n{_selectedModel.Detail}"
            : _selectedModel.Detail;
    }

    private void ModelSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_modelsInitializing || ModelSelector.SelectedItem is not DesktopAiModelChoice choice) return;

        if (choice.OpensSettings)
        {
            _host.ShowAiModelSettingsFromSearch();
            RefreshModelChoices();
            FocusSearch();
            return;
        }

        _selectedModel = choice;
        UpdateModelSelectorHint();
        CollapseReply();
        FocusSearch();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;

        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            CollapseSearchResults();
            return;
        }

        CollapseReply();
        var debounce = new CancellationTokenSource();
        _searchDebounce = debounce;
        _ = RefreshLocalResultsAsync(query, debounce.Token);
    }

    private async Task RefreshLocalResultsAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(85, cancellationToken);
            var results = await _searchIndex.SearchAsync(query, 8, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(SearchBox.Text.Trim(), query, StringComparison.Ordinal)) return;

            SearchResultsList.ItemsSource = results;
            if (results.Count == 0)
            {
                CollapseSearchResults();
                return;
            }

            // Exact/prefix application matches behave like a launcher: typing an
            // app name and pressing Enter opens it. Fuzzy/path results remain
            // unselected so Enter naturally goes to AI instead.
            SearchResultsList.SelectedIndex = results[0].Kind == DesktopSearchResultKind.App && results[0].Score >= 950
                ? 0
                : -1;
            ExpandSearchResults(results.Count);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this query.
        }
        catch
        {
            CollapseSearchResults();
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _activeRequest?.Cancel();
            RestoreIdleState(clearText: true);
            Keyboard.ClearFocus();
            return;
        }

        if (e.Key == Key.Down && SearchResultsPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            MoveResultSelection(1);
            return;
        }

        if (e.Key == Key.Up && SearchResultsPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            MoveResultSelection(-1);
            return;
        }

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;

            var forceAi = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!forceAi && SearchResultsList.SelectedItem is DesktopSearchResult selected)
            {
                OpenLocalResult(selected);
                return;
            }

            await SubmitAsync();
        }
    }

    private void MoveResultSelection(int direction)
    {
        if (SearchResultsList.Items.Count == 0) return;

        var index = SearchResultsList.SelectedIndex;
        if (index < 0)
            index = direction > 0 ? 0 : SearchResultsList.Items.Count - 1;
        else
            index = Math.Clamp(index + direction, 0, SearchResultsList.Items.Count - 1);

        SearchResultsList.SelectedIndex = index;
        SearchResultsList.ScrollIntoView(SearchResultsList.SelectedItem);
    }

    private void SearchResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is DesktopSearchResult selected)
            OpenLocalResult(selected);
    }

    private void OpenLocalResult(DesktopSearchResult result)
    {
        var opened = _searchIndex.Open(result);
        if (!opened)
        {
            ReplyTitle.Text = "无法打开";
            ReplyText.Text = result.Target;
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(240, 125, 125));
            CollapseSearchResults();
            ExpandReply(92);
            return;
        }

        SearchBox.Clear();
        CollapseSearchResults();
        CollapseReply();
        Keyboard.ClearFocus();
    }

    private async Task SubmitAsync()
    {
        var prompt = SearchBox.Text.Trim();
        if (_busy || string.IsNullOrWhiteSpace(prompt)) return;

        if (_selectedModel is null || !_selectedModel.IsAvailable || _selectedModel.Settings is null)
        {
            ShowNoModelHint();
            return;
        }

        _busy = true;
        SearchBox.IsEnabled = false;
        ModelSelector.IsEnabled = false;
        ReplyTitle.Text = $"{_selectedModel.DisplayName} · 正在处理";
        ReplyText.Text = prompt;
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(127, 143, 255));
        CollapseSearchResults();
        ExpandReply();

        var request = new CancellationTokenSource(SearchResponseTimeout);
        _activeRequest = request;

        try
        {
            var reply = await _host.SubmitSearchCommandWithModelAsync(prompt, _selectedModel, request.Token);
            if (request.IsCancellationRequested) return;

            ReplyTitle.Text = _selectedModel.DisplayName;
            ReplyText.Text = string.IsNullOrWhiteSpace(reply) ? "没有返回内容。" : reply;
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(99, 230, 190));
            SearchBox.Clear();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A desktop entry must never look permanently stuck. After 30 seconds
            // (or Escape) cancel the request and restore the compact idle search UI.
            RestoreIdleState(clearText: true);
        }
        catch (Exception error)
        {
            ReplyTitle.Text = $"{_selectedModel.DisplayName} · 未完成";
            ReplyText.Text = error.Message;
            ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(240, 125, 125));
        }
        finally
        {
            if (ReferenceEquals(_activeRequest, request))
            {
                _activeRequest = null;
            }
            request.Dispose();
            _busy = false;
            SearchBox.IsEnabled = true;
            ModelSelector.IsEnabled = true;
            if (IsVisible)
            {
                SearchBox.Focus();
                RefreshPosition();
            }
        }
    }

    private void ShowNoModelHint()
    {
        ReplyTitle.Text = "需要 AI 模型";
        ReplyText.Text = "还没有可用模型。点击搜索框左侧“未配置 AI”，粘贴 API Key 或配置 Ollama / LM Studio 后即可直接对话。应用和文件搜索不受影响。";
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(241, 182, 106));
        CollapseSearchResults();
        ExpandReply(110);
    }

    private void RestoreIdleState(bool clearText)
    {
        if (clearText) SearchBox.Clear();
        ReplyTitle.Text = "图灵";
        ReplyText.Text = string.Empty;
        CollapseSearchResults();
        CollapseReply();
        SearchBox.IsEnabled = true;
        ModelSelector.IsEnabled = true;
        _busy = false;
    }

    private void ExpandSearchResults(int count)
    {
        SearchResultsPanel.Visibility = Visibility.Visible;
        SearchResultsRow.Height = new GridLength(Math.Min(224, 14 + count * 48));
        UpdateExpandedHeight();
    }

    private void CollapseSearchResults()
    {
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        SearchResultsRow.Height = new GridLength(0);
        SearchResultsList.SelectedIndex = -1;
        UpdateExpandedHeight();
    }

    private void ExpandReply(double height = 150)
    {
        ReplyPanel.Visibility = Visibility.Visible;
        ReplyRow.Height = new GridLength(height);
        UpdateExpandedHeight();
    }

    private void CollapseReply()
    {
        ReplyPanel.Visibility = Visibility.Collapsed;
        ReplyRow.Height = new GridLength(0);
        UpdateExpandedHeight();
    }

    private void UpdateExpandedHeight()
    {
        var resultsHeight = SearchResultsPanel.Visibility == Visibility.Visible ? SearchResultsRow.Height.Value : 0;
        var replyHeight = ReplyPanel.Visibility == Visibility.Visible ? ReplyRow.Height.Value : 0;
        Height = CompactHeight + resultsHeight + replyHeight;
        RefreshPosition();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _host.ShowDesktopLibrary();
        RefreshModelChoices();
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
