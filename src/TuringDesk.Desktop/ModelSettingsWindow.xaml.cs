using System.Windows;
using System.Windows.Controls;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class ModelSettingsWindow : Window
{
    private readonly UnifiedModelConfigurationService _modelConfiguration;
    private readonly ModelConnectionProbeService _connectionProbe = new();
    private bool _initializing = true;

    public ModelSettings? SavedSettings { get; private set; }

    public ModelSettingsWindow(ModelSettingsStore store, ModelSettings initial, string? apiKey)
    {
        InitializeComponent();
        _modelConfiguration = new UnifiedModelConfigurationService(store);

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
            StatusText.Text = "正在直接测试模型连接…";
            var result = await _connectionProbe.ProbeAsync(settings, ApiKeyBox.Password);
            StatusText.Text = $"连接成功：{result}";
        }
        catch (Exception error)
        {
            StatusText.Text = $"连接测试失败：{error.Message}";
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = BuildSettings();
            var key = ApiKeyBox.Password;
            StatusText.Text = "正在同步 TuringDesk 与 DeepSeek Harness…";

            SavedSettings = await _modelConfiguration.ApplyAndSaveAsync(settings, key);
            DialogResult = true;
        }
        catch (Exception error)
        {
            StatusText.Text = $"配置未完成：{error.Message}";
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
