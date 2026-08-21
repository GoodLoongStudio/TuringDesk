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
    private static readonly TimeSpan FileSearchDebounce = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan SearchResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly MainWindow _host;
    private readonly DesktopSearchIndexService _searchIndex;
    private readonly DesktopQuickAnswerService _quickAnswer = new();
    private readonly WindowsSpeechService _speech = new();
    private HwndSource? _source;
    private bool _hotkeyRegistered;
    private bool _fallbackHotkeyRegistered;
    private bool _busy;
    private bool _modelsInitializing;
    private CancellationTokenSource? _activeRequest;
    private CancellationTokenSource? _searchPipeline;
    private long _searchGeneration;
    private string? _pendingDeepQuery;
    private string? _pendingRetryQuery;
    private DesktopAiModelChoice? _selectedModel;
    private System.Windows.Controls.Button? _retryButton;
    private System.Windows.Controls.Button? _newConversationButton;

    public DesktopSearchBarWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        InitializeL3ReplyActions();
        _searchIndex = new DesktopSearchIndexService();
        _speech.Recognized += OnSpeechRecognized;

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

        Activated += (_, _) => Dispatcher.BeginInvoke(
            new Action(RefreshPosition),
            DispatcherPriority.ContextIdle);
        Deactivated += (_, _) => RefreshPosition();
        Closed += OnClosed;
    }

    private void InitializeL3ReplyActions()
    {
        if (ReplyPanel.Child is not System.Windows.Controls.Grid grid) return;

        grid.Children.Remove(DeepProcessButton);
        var actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(actions, 2);

        DeepProcessButton.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(DeepProcessButton);

        _retryButton = new System.Windows.Controls.Button
        {
            Content = "重试",
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(13, 6, 13, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold
        };
        _retryButton.Click += Retry_Click;
        actions.Children.Add(_retryButton);

        _newConversationButton = new System.Windows.Controls.Button
        {
            Content = "新对话",
            Padding = new Thickness(13, 6, 13, 6),
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold
        };
        _newConversationButton.Click += ResetConversation_Click;
        actions.Children.Add(_newConversationButton);
        grid.Children.Add(actions);
    }

    internal void RefreshPosition()
    {
        if (!IsInitialized) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = DisplayManager.GetPrimary();
        var width = Math.Max(1, (int)Math.Round(Width * monitor.ScaleX));
        var height = Math.Max(1, (int)Math.Round(Height * monitor.ScaleY));
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
        // Do not Dispose a CTS while its async operation can still be registering
        // callbacks against the token. The owning async method disposes it in finally.
        _activeRequest?.Cancel();
        _activeRequest = null;
        CancelSearchPipeline();
        _searchIndex.Dispose();
        _speech.Dispose();

        DesktopSearchReservedArea.Clear();
        var hwnd = new WindowInteropHelper(this).Handle;
        if ((_hotkeyRegistered || _fallbackHotkeyRegistered) && hwnd != IntPtr.Zero)
            _ = UnregisterHotKey(hwnd, HotkeyId);
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
            ModelSelector.ToolTip = "没有可用 AI 模型；点击配置。应用/文件搜索和计算仍可使用。";
            return;
        }

        ModelSelector.ToolTip = _selectedModel.IsAvailable
            ? $"轻量翻译/解释模型：{_selectedModel.DisplayName}\n{_selectedModel.Detail}\n复杂任务只有点击“深度处理”才启动 Harness。"
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

        CancelSearchPipeline();
        var generation = Interlocked.Increment(ref _searchGeneration);
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            CollapseSearchResults();
            CollapseReply();
            return;
        }

        CollapseReply();

        // Level 1: RAM-only application/pinyin prefix search. It is intentionally
        // synchronous because this path does no disk, registry, COM or IPC work.
        var apps = _searchIndex.SearchApps(query, 5);
        ApplySearchResults(query, generation, apps);

        // Level 2 owns its CTS. A new keystroke only Cancels the old source; the
        // corresponding async task disposes it after all token users have unwound.
        var pipeline = new CancellationTokenSource();
        _searchPipeline = pipeline;
        _ = StreamFileResultsAsync(query, apps, generation, pipeline);
    }

    private async Task StreamFileResultsAsync(
        string query,
        IReadOnlyList<DesktopSearchResult> apps,
        long generation,
        CancellationTokenSource pipeline)
    {
        try
        {
            // L1 has already painted. Delay only the heavier file tier so a burst of
            // keyboard input collapses into one file query rather than one Task.Run
            // per character.
            await Task.Delay(FileSearchDebounce, pipeline.Token);
            var files = await _searchIndex.SearchFilesAsync(query, 6, pipeline.Token);
            pipeline.Token.ThrowIfCancellationRequested();

            var merged = apps
                .Concat(files)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Level)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(8)
                .ToArray();

            ApplySearchResults(query, generation, merged);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this query.
        }
        catch (ObjectDisposedException)
        {
            // Window teardown can race the final continuation; no UI update is valid.
        }
        catch
        {
            // Keep already-rendered Level 1 app results if the file provider fails.
        }
        finally
        {
            if (ReferenceEquals(_searchPipeline, pipeline))
                Interlocked.CompareExchange(ref _searchPipeline, null, pipeline);
            pipeline.Dispose();
        }
    }

    private void ApplySearchResults(
        string query,
        long generation,
        IReadOnlyList<DesktopSearchResult> results)
    {
        if (generation != Volatile.Read(ref _searchGeneration)) return;
        if (!string.Equals(SearchBox.Text.Trim(), query, StringComparison.Ordinal)) return;

        SearchResultsList.ItemsSource = results;
        if (results.Count == 0)
        {
            CollapseSearchResults();
            return;
        }

        SearchResultsList.SelectedIndex =
            results[0].Kind == DesktopSearchResultKind.App && results[0].Score >= 990
                ? 0
                : -1;
        ExpandSearchResults(results.Count);
    }

    private void CancelSearchPipeline()
    {
        var previous = Interlocked.Exchange(ref _searchPipeline, null);
        if (previous is null) return;
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
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

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                OpenDeepProcessing(SearchBox.Text.Trim());
                return;
            }

            if (SearchResultsList.SelectedItem is DesktopSearchResult selected)
            {
                OpenLocalResult(selected);
                return;
            }

            await SubmitQuickAsync();
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
            SetL3ActionState(retry: false, deep: false);
            CollapseSearchResults();
            ExpandReply(92);
            return;
        }

        SearchBox.Clear();
        CollapseSearchResults();
        CollapseReply();
        Keyboard.ClearFocus();
    }

    /// <summary>
    /// Level 3 only. This method must never start Node or the Harness workbench.
    /// Complex requests are converted into an explicit deep-processing offer.
    /// </summary>
    private async Task SubmitQuickAsync()
    {
        var prompt = SearchBox.Text.Trim();
        if (_busy || string.IsNullOrWhiteSpace(prompt)) return;

        _busy = true;
        _pendingRetryQuery = null;
        SearchBox.IsReadOnly = true;
        ModelSelector.IsEnabled = false;
        if (_newConversationButton is not null) _newConversationButton.IsEnabled = false;
        ReplyTitle.Text = "轻量直答 · 正在处理";
        ReplyText.Text = prompt;
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(127, 143, 255));
        SetL3ActionState(retry: false, deep: false);
        CollapseSearchResults();
        ExpandReply();

        var request = new CancellationTokenSource(SearchResponseTimeout);
        _activeRequest = request;

        try
        {
            var result = await _quickAnswer.TryAnswerAsync(prompt, _selectedModel, request.Token);
            if (request.IsCancellationRequested) return;

            switch (result.Disposition)
            {
                case DesktopQuickAnswerDisposition.Answered:
                    if (result.Title.StartsWith("CLI 连接失败", StringComparison.Ordinal))
                    {
                        ShowL3Failure(prompt, result.Title, result.Message);
                        break;
                    }

                    _pendingDeepQuery = null;
                    _pendingRetryQuery = null;
                    ReplyTitle.Text = result.Title;
                    ReplyText.Text = result.Message;
                    ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(99, 230, 190));
                    SetL3ActionState(retry: false, deep: false);
                    ExpandReply();
                    break;

                case DesktopQuickAnswerDisposition.RequiresModel:
                    _pendingDeepQuery = null;
                    _pendingRetryQuery = null;
                    ReplyTitle.Text = result.Title;
                    ReplyText.Text = result.Message;
                    ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(241, 182, 106));
                    SetL3ActionState(retry: false, deep: false);
                    ExpandReply(120);
                    break;

                default:
                    ShowDeepProcessingOffer(prompt, result.Title, result.Message);
                    break;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            RestoreIdleState(clearText: false);
        }
        catch (Exception error)
        {
            ShowL3Failure(prompt, "CLI 未完成", error.Message);
        }
        finally
        {
            if (ReferenceEquals(_activeRequest, request))
                _activeRequest = null;
            request.Dispose();
            _busy = false;
            SearchBox.IsReadOnly = false;
            ModelSelector.IsEnabled = true;
            if (_newConversationButton is not null) _newConversationButton.IsEnabled = true;
            if (IsVisible)
            {
                SearchBox.Focus();
                RefreshPosition();
            }
        }
    }

    private void ShowL3Failure(string prompt, string title, string message)
    {
        _pendingRetryQuery = prompt;
        _pendingDeepQuery = null;
        ReplyTitle.Text = title;
        ReplyText.Text = message;
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(225, 91, 91));
        SetL3ActionState(retry: true, deep: false);
        CollapseSearchResults();
        ExpandReply(175);
    }

    private void ShowDeepProcessingOffer(string prompt, string title, string message)
    {
        _pendingRetryQuery = null;
        _pendingDeepQuery = prompt;
        ReplyTitle.Text = title;
        ReplyText.Text = message;
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(99, 102, 241));
        SetL3ActionState(retry: false, deep: true);
        CollapseSearchResults();
        ExpandReply(175);
    }

    private void SetL3ActionState(bool retry, bool deep)
    {
        if (_retryButton is not null)
            _retryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        DeepProcessButton.Visibility = deep ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_pendingRetryQuery)) return;
        var prompt = _pendingRetryQuery;
        if (!string.Equals(SearchBox.Text, prompt, StringComparison.Ordinal))
        {
            SearchBox.Text = prompt;
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
        await SubmitQuickAsync();
    }

    private void ResetConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _quickAnswer.ResetConversation();
        _pendingRetryQuery = null;
        _pendingDeepQuery = null;
        SearchBox.Clear();
        ReplyTitle.Text = "CLI · 新对话";
        ReplyText.Text = "已清除当前轻量对话上下文。下一条消息会从新的会话开始。";
        ReplyDot.Fill = new SolidColorBrush(Color.FromRgb(99, 230, 190));
        SetL3ActionState(retry: false, deep: false);
        CollapseSearchResults();
        ExpandReply(105);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void DeepProcess_Click(object sender, RoutedEventArgs e) =>
        OpenDeepProcessing(_pendingDeepQuery ?? SearchBox.Text.Trim());

    private void OpenDeepProcessing(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        _pendingDeepQuery = query;
        _pendingRetryQuery = null;
        _host.ShowHarnessConsoleFromSearch(query);
        CollapseReply();
        CollapseSearchResults();
    }

    private void RestoreIdleState(bool clearText)
    {
        if (clearText) SearchBox.Clear();
        ReplyTitle.Text = "图灵";
        ReplyText.Text = string.Empty;
        _pendingDeepQuery = null;
        _pendingRetryQuery = null;
        SetL3ActionState(retry: false, deep: false);
        CollapseSearchResults();
        CollapseReply();
        SearchBox.IsReadOnly = false;
        SearchBox.IsEnabled = true;
        ModelSelector.IsEnabled = true;
        if (_newConversationButton is not null) _newConversationButton.IsEnabled = true;
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
        _pendingDeepQuery = null;
        _pendingRetryQuery = null;
        SetL3ActionState(retry: false, deep: false);
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

    private async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_speech.IsListening)
        {
            await _speech.StopAsync();
            return;
        }

        var started = await _speech.StartAsync();
        if (started)
        {
            ShellNotificationService.Publish(
                "语音已启动",
                $"正在使用 {_speech.RecognizerName} ({_speech.RecognizerCulture})。直接说话，结果会进入搜索框。",
                "voice");
        }
        else
        {
            ShellNotificationService.Publish(
                "语音不可用",
                "当前设备没有安装 Windows 语音识别器。键盘输入仍然可用。",
                "warning");
        }
    }

    private void OnSpeechRecognized(string text, float confidence)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (confidence < 0.30f) return;
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }), DispatcherPriority.Normal);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
