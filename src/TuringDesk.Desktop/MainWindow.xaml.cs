using System.Windows;
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
        InitializeComponent();
        _speech.Recognized += OnSpeechRecognized;
        _speech.StatusChanged += OnSpeechStatusChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetAgentState("正在准备桌面能力…", "启动本地能力层、AI Runtime 与常驻语音。", Color.FromRgb(241, 182, 106));

        _capabilities = new CapabilityServer(_launcher, _windows, AddActivity);
        try
        {
            await _capabilities.StartAsync();
            AddActivity("system", $"Capability server online ({_capabilities.BaseUrl}).");
        }
        catch (Exception error)
        {
            AddActivity("system", $"Capability server failed: {error.Message}");
            SetAgentState("部分能力不可用", "Windows Capability Server 启动失败，普通应用入口仍可使用。", Color.FromRgb(240, 125, 125));
        }

        var health = await _runtime.GetHealthAsync();
        if (health is not null)
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(84, 214, 138));
            RuntimeStatus.Text = $"Runtime {health.Mode}";
            AddActivity("system", $"AI runtime connected ({health.Mode}).");
        }
        else
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(240, 125, 125));
            RuntimeStatus.Text = "Runtime offline";
            AddActivity("system", "Runtime is offline. Quick launch buttons still work.");
            SetAgentState("AI Runtime 离线", "你仍然可以像普通桌面一样启动应用；Agent 指令暂时不可用。", Color.FromRgb(240, 125, 125));
        }

        _modelSettings = await _modelStore.LoadAsync();
        var key = _modelStore.LoadApiKey();
        if (health is not null)
        {
            var applied = await _runtime.ConfigureModelAsync(_modelSettings, key);
            if (applied is null && _modelSettings.ProviderId != "mock")
            {
                AddActivity("model", "Saved model configuration could not be applied; Runtime remains available in its current mode.");
            }
        }
        UpdateModelStatus();

        var voiceReady = await _speech.StartAsync();
        if (voiceReady)
        {
            AddActivity("voice", $"Always-on Windows speech recognition ready ({_speech.RecognizerName}, {_speech.RecognizerCulture}). Say “图灵桌面” before a command.");
            VoiceStateText.Text = "已常驻 · 说“图灵桌面”唤醒";
            SetAgentState("随时可以叫我", "说“图灵桌面”后直接下命令，或继续用鼠标和键盘操作。", Color.FromRgb(84, 214, 138));
        }
        else
        {
            AddActivity("voice", "Windows speech recognition is unavailable. Keyboard input remains available.");
            VoiceStateText.Text = "当前设备不可用 · 可继续键盘输入";
            if (health is not null)
            {
                SetAgentState("Agent 已就绪", "常驻语音不可用，但文字指令与普通桌面操作不受影响。", Color.FromRgb(84, 214, 138));
            }
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _speech.Dispose();
        if (_capabilities is not null)
        {
            await _capabilities.DisposeAsync();
            _capabilities = null;
        }
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

        var reply = await _runtime.ChatAsync(text);
        if (reply is null)
        {
            const string offline = "Runtime is offline or the selected model could not answer.";
            AddActivity("ai", offline);
            SetAgentState("这次没有完成", "AI Runtime 离线或当前模型无法响应。", Color.FromRgb(240, 125, 125));
            return;
        }

        AddActivity("ai", reply);
        WorkspaceSuggestion.Text = TrimForUi(reply, 180);
        SetAgentState("已完成", TrimForUi(reply, 150), Color.FromRgb(84, 214, 138));
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
        _modelSettings = await _modelStore.LoadAsync();
        var key = _modelStore.LoadApiKey();
        var dialog = new ModelSettingsWindow(_runtime, _modelStore, _modelSettings, key) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SavedSettings is not null)
        {
            _modelSettings = dialog.SavedSettings;
            UpdateModelStatus();
            var providerName = ModelProviderPresets.Find(_modelSettings.ProviderId).Name;
            AddActivity("model", $"Model switched to {providerName} / {_modelSettings.Model}.");
            SetAgentState("模型已切换", $"现在使用 {providerName} · {_modelSettings.Model}", Color.FromRgb(84, 214, 138));
        }
    }

    private void UpdateModelStatus()
    {
        var preset = ModelProviderPresets.Find(_modelSettings.ProviderId);
        ModelStatus.Text = _modelSettings.ProviderId == "mock"
            ? "Model: Mock"
            : $"Model: {preset.Name} · {_modelSettings.Model}";
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
