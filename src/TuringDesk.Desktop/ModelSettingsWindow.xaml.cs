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
        ProviderBox.SelectedItem = initial.IsConfigured
            ? ModelProviderPresets.Find(initial.ProviderId)
            : ModelProviderPresets.All[0];
        BaseUrlBox.Text = initial.IsConfigured ? initial.BaseUrl : ModelProviderPresets.All[0].BaseUrl;
        ModelBox.Text = initial.IsConfigured ? initial.Model : ModelProviderPresets.All[0].Model;
        ApiKeyBox.Password = apiKey ?? string.Empty;
        ProviderHint.Text = ((ModelProviderPreset)ProviderBox.SelectedItem).Hint;
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
        }

        ApplyProviderState();
    }

    private void ApplyProviderState()
    {
        if (ProviderBox.SelectedItem is not ModelProviderPreset preset) return;
        ApiKeyBox.IsEnabled = true;
        BaseUrlBox.IsEnabled = !preset.Id.Equals("deepseek", StringComparison.OrdinalIgnoreCase);
        ModelBox.IsEnabled = true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = BuildSettings();
            StatusText.Text = "正在由 TuringDesk 原生 HttpClient 直接测试模型连接…";
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
            StatusText.Text = "正在保存模型配置，并同步给 TuringDesk L3 与 DeepSeek Harness…";

            SavedSettings = await _modelConfiguration.ApplyAndSaveAsync(settings, key);

            // This window is opened modally from Desktop Settings and non-modally
            // from the top search bar. DialogResult is illegal for Show(), while
            // Close() safely completes both entry paths.
            Close();
        }
        catch (Exception error)
        {
            StatusText.Text = $"配置未完成：{error.Message}";
        }
    }

    private ModelSettings BuildSettings()
    {
        if (ProviderBox.SelectedItem is not ModelProviderPreset preset)
            throw new InvalidOperationException("请选择模型提供商。");

        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        var key = ApiKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("请填写模型 ID。");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Base URL 必须是 http:// 或 https:// 地址。");

        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("这个提供商需要 API Key。");

        return new ModelSettings(preset.Id, "direct", baseUrl, model, !string.IsNullOrWhiteSpace(key));
    }
}
