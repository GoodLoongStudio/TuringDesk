using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly RuntimeClient _runtime = new();
    private readonly AppLauncher _launcher = new();
    private readonly WindowManager _windows = new();
    private readonly ModelSettingsStore _modelStore = new();
    private readonly WindowsSpeechService _speech = new();
    private CapabilityServer? _capabilities;
    private DateTime _voiceCommandUntilUtc = DateTime.MinValue;
    private ModelSettings _modelSettings = ModelSettings.Default;

    public MainWindow()
    {
        ShellThemeService.Apply(new ShellSettingsStore().Load().Appearance);
        InitializeComponent();
        _speech.Recognized += OnSpeechRecognized;
        _speech.StatusChanged += OnSpeechStatusChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetAgentState(
            "正在准备轻量桌面层…",
            "本地搜索、桌面能力与常驻语音先就绪；AI Runtime / DeepSeek Harness 按需启动。",
            Color.FromRgb(241, 182, 106));

        // Keep the native capability endpoint available for a future MCP lease,
        // but deliberately leave Node.js Runtime and DeepSeek Harness cold.
        _capabilities = new CapabilityServer(_launcher, _windows, AddActivity);
        try
        {
            await _capabilities.StartAsync();
            AddActivity("system", $"Capability server online ({_capabilities.BaseUrl}).");
        }
        catch (Exception error)
        {
            AddActivity("system", $"Capability server failed: {error.Message}");
            SetAgentState("部分能力不可用", "Windows Capability Server 启动失败，普通应用与文件搜索仍可使用。", Color.FromRgb(240, 125, 125));
        }

        // Passive probe only: RuntimeClient.GetHealthAsync never wakes a sleeping
        // process. A pre-existing ShellHost/external Runtime can still be surfaced.
        var health = await _runtime.GetHealthAsync();
        if (health is not null)
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(84, 214, 138));
            RuntimeStatus.Text = $"Runtime {health.Mode}";
            AddActivity("system", $"Existing AI runtime detected ({health.Mode}); no new process was started.");
        }
        else
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            RuntimeStatus.Text = "Runtime 按需待命";
            AddActivity("system", "AI Runtime is cold and will start only for Agent/Harness work.");
        }

        // Persisted model state is cheap local configuration. Do not push it into
        // Runtime at startup: non-mock Runtime model configuration initializes the
        // Harness gateway and defeats lazy loading.
        _modelSettings = await _modelStore.LoadAsync();
        UpdateModelStatus();

        var voiceReady = await _speech.StartAsync();
        if (voiceReady)
        {
            AddActivity("voice", $"Always-on Windows speech recognition ready ({_speech.RecognizerName}, {_speech.RecognizerCulture}). Say “图灵桌面” before a command.");
            VoiceStateText.Text = "已常驻 · 说“图灵桌面”唤醒";
            SetAgentState(
                "桌面已就绪",
                "应用/文件搜索与语音已就绪；复杂任务会在需要时热启动 Agent。",
                Color.FromRgb(84, 214, 138));
        }
        else
        {
            AddActivity("voice", "Windows speech recognition is unavailable. Keyboard input remains available.");
            VoiceStateText.Text = "当前设备不可用 · 可继续键盘输入";
            SetAgentState(
                "桌面已就绪",
                "应用/文件搜索已就绪；Agent/Harness 保持休眠直到真正需要。",
                Color.FromRgb(84, 214, 138));
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _speech.Dispose();
        AgentFloatingCardsService.Hide();
        if (_capabilities is not null)
        {
            await _capabilities.DisposeAsync();
            _capabilities = null;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // Window state can change between the mouse event and DragMove.
        }
    }

    private void CommandBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeButton is null) return;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage, NavHomeButton);
    private void NavApps_Click(object sender, RoutedEventArgs e) => ShowPage(AppsPage, NavAppsButton);
    private void NavWorkspaces_Click(object sender, RoutedEventArgs e) => ShowPage(WorkspacesPage, NavWorkspacesButton);
    private void NavTasks_Click(object sender, RoutedEventArgs e) => ShowPage(TasksPage, NavTasksButton);
    private void NavMemory_Click(object sender, RoutedEventArgs e) => ShowPage(MemoryPage, NavMemoryButton);

    private void ShowPage(ScrollViewer page, Button selectedButton)
    {
        foreach (var candidate in new[] { HomePage, AppsPage, WorkspacesPage, TasksPage, MemoryPage })
        {
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var button in new[] { NavHomeButton, NavAppsButton, NavWorkspacesButton, NavTasksButton, NavMemoryButton })
        {
            button.Style = (Style)FindResource(button == selectedButton ? "TopNavButtonActiveStyle" : "TopNavButtonStyle");
        }
    }

    private void AgentPanel_Click(object sender, RoutedEventArgs e)
    {
        AgentPanel.Visibility = AgentPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void Chrome_Click(object sender, RoutedEventArgs e) => await LaunchAsync("chrome");
    private async void VSCode_Click(object sender, RoutedEventArgs e) => await LaunchAsync("code");
    private async void Terminal_Click(object sender, RoutedEventArgs e) => await LaunchAsync("terminal");

    private async Task LaunchAsync(string app)
    {
        SetAgentState("正在打开应用…", $"启动 {app}，你仍然可以随时手动接管。", Color.FromRgb(135, 150, 255));
        var launched = await _launcher.LaunchAsync(app);
        AddActivity("app", launched ? $"Launched {app}." : $"Could not launch {app}.");
        SetAgentState(
            launched ? "应用已打开" : "应用启动失败",
            launched ? $"{app} 已交还给 Windows 原生窗口管理。" : $"没有找到或无法启动 {app}。",
            launched ? Color.FromRgb(84, 214, 138) : Color.FromRgb(240, 125, 125));
    }

    private void FocusAgent_Click(object sender, RoutedEventArgs e)
    {
        CommandBox.Focus();
        Keyboard.Focus(CommandBox);
        SetAgentState("我在听", "直接描述你想完成的事情，不需要记命令格式。", Color.FromRgb(135, 150, 255));
    }

    private async void Ask_Click(object sender, RoutedEventArgs e) => await SubmitCommandAsync();

    private async void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitCommandAsync();
        }
    }

    private async Task SubmitCommandAsync()
    {
        var text = CommandBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        CommandBox.Clear();
        AddActivity("you", text);
        SetAgentState("正在理解你的请求…", TrimForUi(text, 120), Color.FromRgb(135, 150, 255));
        AgentFloatingCardsService.Begin(DisplayManager.GetPrimary(), text);

        // RuntimeClient owns lazy startup and a request lease. This path is an
        // explicit Agent surface, so waking Runtime/Harness here is intentional.
        var reply = await _runtime.ChatAsync(text);
        if (reply is null)
        {
            const string offline = "Runtime is offline or the selected model could not answer.";
            AddActivity("ai", offline);
            SetAgentState("这次没有完成", "AI Runtime 离线或当前模型无法响应。", Color.FromRgb(240, 125, 125));
            AgentFloatingCardsService.Fail("AI Runtime 离线或当前模型无法响应。你可以在桌面 DIY 中心检查快捷模型连接或 Harness 状态。");
            return;
        }

        AddActivity("ai", reply);
        WorkspaceSuggestion.Text = TrimForUi(reply, 180);
        SetAgentState("已完成", TrimForUi(reply, 150), Color.FromRgb(84, 214, 138));
        AgentFloatingCardsService.Complete(reply);
    }

    private async void SpeechToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_speech.IsListening)
        {
            await _speech.StopAsync();
            SpeechButton.Content = "恢复";
            MicButton.Content = "\uE720";
            VoiceStateText.Text = "已暂停 · 点击恢复常驻语音";
            SetAgentState("语音已暂停", "键盘、鼠标和文字 Agent 指令仍然可用。", Color.FromRgb(241, 182, 106));
        }
        else
        {
            await _speech.StartAsync();
        }
    }

    private void OnSpeechStatusChanged(string status)
    {
        Dispatcher.Invoke(() =>
        {
            SpeechButton.Content = _speech.IsListening ? "暂停" : "恢复";
            MicButton.Content = "\uE720";
            MicButton.Opacity = _speech.IsListening ? 1.0 : 0.55;
            VoiceStateText.Text = _speech.IsListening
                ? "已常驻 · 说“图灵桌面”唤醒"
                : $"{status} · 点击恢复";
        });
    }

    private void OnSpeechRecognized(string text, float confidence)
    {
        if (confidence < 0.30f) return;
        Dispatcher.InvokeAsync(async () => await HandleSpeechAsync(text, confidence));
    }

    private async Task HandleSpeechAsync(string text, float confidence)
    {
        var remainder = StripWakeWord(text);
        if (remainder is not null)
        {
            AddActivity("voice", $"Wake phrase heard ({confidence:P0}): {text}");
            if (string.IsNullOrWhiteSpace(remainder))
            {
                _voiceCommandUntilUtc = DateTime.UtcNow.AddSeconds(10);
                SpeechButton.Content = "正在听";
                VoiceStateText.Text = "已唤醒 · 等待你的命令";
                SetAgentState("我在听", "继续说你的命令，10 秒内无需再次说唤醒词。", Color.FromRgb(135, 150, 255));
                CommandBox.Focus();
                return;
            }

            await SubmitVoiceCommandAsync(remainder);
            return;
        }

        if (DateTime.UtcNow <= _voiceCommandUntilUtc)
        {
            _voiceCommandUntilUtc = DateTime.MinValue;
            await SubmitVoiceCommandAsync(text);
        }
    }

    private async Task SubmitVoiceCommandAsync(string command)
    {
        command = command.Trim().TrimStart('，', ',', '。', '.', ':', '：', ' ');
        if (string.IsNullOrWhiteSpace(command)) return;

        CommandBox.Text = command;
        AddActivity("voice", command);
        SetAgentState("听到了", TrimForUi(command, 120), Color.FromRgb(135, 150, 255));
        await SubmitCommandAsync();
    }

    private static string? StripWakeWord(string text)
    {
        var value = text.Trim();
        foreach (var wake in new[] { "图灵桌面", "图灵", "turing desk", "turing" })
        {
            if (value.StartsWith(wake, StringComparison.OrdinalIgnoreCase))
            {
                return value[wake.Length..].Trim();
            }
        }
        return null;
    }

    private async void ModelSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DesktopDiyCenterWindow(_runtime, _modelStore) { Owner = this };
        dialog.ShowDialog();
        _modelSettings = await _modelStore.LoadAsync();
        UpdateModelStatus();
        ShellThemeService.Apply(new ShellSettingsStore().Load().Appearance);
        AddActivity("settings", "Desktop DIY Center closed; live shell appearance settings remain applied.");
    }

    internal void ShowDiyCenter()
    {
        var dialog = new DesktopDiyCenterWindow(_runtime, _modelStore) { Owner = IsVisible ? this : null };
        dialog.ShowDialog();
        ShellThemeService.Apply(new ShellSettingsStore().Load().Appearance);
    }

    private void UpdateModelStatus()
    {
        var preset = ModelProviderPresets.Find(_modelSettings.ProviderId);
        ModelStatus.Text = _modelSettings.ProviderId == "mock"
            ? "Mock"
            : $"{preset.Name} · {_modelSettings.Model}";
    }

    private void SetAgentState(string title, string detail, Color color)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetAgentState(title, detail, color));
            return;
        }

        AgentStateTitle.Text = title;
        AgentStateDetail.Text = detail;
        AgentStateDot.Fill = new SolidColorBrush(color);
        AgentStatusText.Text = TrimForUi(title, 22);
    }

    private static string TrimForUi(string text, int maxLength)
    {
        var value = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= maxLength ? value : $"{value[..maxLength]}…";
    }

    private void AddActivity(string source, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddActivity(source, message));
            return;
        }

        ActivityList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {source}: {message}");
    }
}
