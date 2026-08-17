using System.Windows;
using System.Windows.Controls;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class ModelSettingsWindow : Window
{
    private readonly RuntimeClient _runtime;
    private readonly ModelSettingsStore _store;
    private bool _initializing = true;

    public ModelSettings? SavedSettings { get; private set; }

    public ModelSettingsWindow(RuntimeClient runtime, ModelSettingsStore store, ModelSettings initial, string? apiKey)
    {
        InitializeComponent();
        _runtime = runtime;
        _store = store;

        ProviderBox.ItemsSource = ModelProviderPresets.All;
        ProviderBox.SelectedItem = ModelProviderPresets.Find(initial.ProviderId);
        BaseUrlBox.Text = initial.BaseUrl;
        ModelBox.Text = initial.Model;
        ApiKeyBox.Password = apiKey ?? string.Empty;
        ProviderHint.Text = ModelProviderPresets.Find(initial.ProviderId).Hint;
        _initializing = false;
        ApplyProviderState();
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ModelProviderPreset preset) return;
        ProviderHint.Text = preset.Hint;

        if (!_initializing)
        {
            BaseUrlBox.Text = preset.BaseUrl;
            ModelBox.Text = preset.Model;
            if (preset.Id == "mock") ApiKeyBox.Clear();
        }

        ApplyProviderState();
    }

    private void ApplyProviderState()
    {
        if (ProviderBox.SelectedItem is not ModelProviderPreset preset) return;
        ApiKeyBox.IsEnabled = preset.Id != "mock";
        BaseUrlBox.IsEnabled = preset.Id is not "mock" and not "deepseek";
        ModelBox.IsEnabled = preset.Id != "mock";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = BuildSettings();
            StatusText.Text = "正在应用配置并测试快捷 Agent…";
            var configured = await _runtime.ConfigureModelAsync(settings, ApiKeyBox.Password);
            if (configured is null)
            {
                StatusText.Text = "Runtime 没有接受配置。请确认 TuringDesk Runtime 正在运行。";
                return;
            }

            var result = await _runtime.TestModelAsync();
            StatusText.Text = result is null
                ? "连接测试失败。请检查 Base URL、模型 ID 和 API Key。"
                : $"连接成功：{result}";
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = BuildSettings();
            var key = ApiKeyBox.Password;
            StatusText.Text = "正在保存统一模型配置并同步 DeepSeek Harness…";

            var applied = await _runtime.ConfigureModelAsync(settings, key);
            if (applied is null)
            {
                StatusText.Text = "Runtime 配置失败，暂未保存。";
                return;
            }

            // Persist exactly once. Both the native runtime and bundled Harness
            // read this same settings file + Windows Credential Manager secret.
            await _store.SaveAsync(settings, key);

            // Harness may already have started during App bootstrap. Restart the
            // owned process so the freshly saved shared API key/config becomes
            // active immediately instead of requiring an application restart.
            try
            {
                await HarnessWebUiService.RestartWithSavedConfigurationAsync();
            }
            catch (Exception harnessError)
            {
                StatusText.Text = $"模型已保存，但 Harness 重新加载失败：{harnessError.Message}";
                return;
            }

            SavedSettings = settings with { HasApiKey = !string.IsNullOrWhiteSpace(key) };
            DialogResult = true;
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private ModelSettings BuildSettings()
    {
        if (ProviderBox.SelectedItem is not ModelProviderPreset preset)
        {
            throw new InvalidOperationException("请选择模型提供商。");
        }

        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        var key = ApiKeyBox.Password.Trim();

        if (preset.Id != "mock" && string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("请填写模型 ID。");
        }

        if (preset.Id is "ollama" or "lmstudio" or "openai-compatible")
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("Base URL 必须是 http:// 或 https:// 地址。");
            }
        }

        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("这个提供商需要 API Key。");
        }

        return new ModelSettings(preset.Id, preset.Mode, baseUrl, model, !string.IsNullOrWhiteSpace(key));
    }
}
