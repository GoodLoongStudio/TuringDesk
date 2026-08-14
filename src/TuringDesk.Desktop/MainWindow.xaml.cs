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
        _capabilities = new CapabilityServer(_launcher, _windows, AddActivity);
        try
        {
            await _capabilities.StartAsync();
            AddActivity("system", $"Capability server online ({_capabilities.BaseUrl}).");
        }
        catch (Exception error)
        {
            AddActivity("system", $"Capability server failed: {error.Message}");
        }

        var health = await _runtime.GetHealthAsync();
        if (health is not null)
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            RuntimeStatus.Text = $"Runtime {health.Mode}";
            AddActivity("system", $"AI runtime connected ({health.Mode}).");
        }
        else
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            RuntimeStatus.Text = "Runtime offline";
            AddActivity("system", "Runtime is offline. Quick launch buttons still work.");
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
        }
        else
        {
            AddActivity("voice", "Windows speech recognition is unavailable. Keyboard input remains available.");
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
        var launched = await _launcher.LaunchAsync(app);
        AddActivity("app", launched ? $"Launched {app}." : $"Could not launch {app}.");
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
        var reply = await _runtime.ChatAsync(text);
        AddActivity("ai", reply ?? "Runtime is offline or the selected model could not answer.");
    }

    private async void SpeechToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_speech.IsListening)
        {
            await _speech.StopAsync();
            SpeechButton.Content = "🎙 Voice paused";
            MicButton.Content = "🎙";
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
            SpeechButton.Content = $"🎙 {status}";
            MicButton.Opacity = _speech.IsListening ? 1.0 : 0.55;
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
                SpeechButton.Content = "🎙 等待命令…";
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
            AddActivity("model", $"Model switched to {ModelProviderPresets.Find(_modelSettings.ProviderId).Name} / {_modelSettings.Model}.");
        }
    }

    private void UpdateModelStatus()
    {
        var preset = ModelProviderPresets.Find(_modelSettings.ProviderId);
        ModelStatus.Text = _modelSettings.ProviderId == "mock"
            ? "Model: Mock"
            : $"Model: {preset.Name} · {_modelSettings.Model}";
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
