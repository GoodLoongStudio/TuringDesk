using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class FirstRunSetupWindow : Window
{
    private readonly RuntimeClient _runtime;
    private readonly ModelSettingsStore _modelStore;
    private readonly ShellSettingsStore _shellStore = new();
    private readonly OnboardingStateStore _onboardingStore = new();
    private string _selectedSceneId = "builtin:aurora";
    private bool _loadingProvider;

    public FirstRunSetupWindow(RuntimeClient runtime, ModelSettingsStore modelStore)
    {
        _runtime = runtime;
        _modelStore = modelStore;
        InitializeComponent();
        Loaded += (_, _) => ApplyProviderUi("deepseek");
    }

    private void Scene_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id }) _selectedSceneId = id;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProvider || !IsLoaded) return;
        ApplyProviderUi(SelectedProvider());
    }

    private void ApplyProviderUi(string providerId)
    {
        _loadingProvider = true;
        try
        {
            var preset = ModelProviderPresets.Find(providerId);
            var isRemoteCustom = providerId == "openai-compatible";
            var needsKey = preset.RequiresApiKey || isRemoteCustom;

            ApiKeyLabel.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
            ApiKeyBox.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
            BaseUrlLabel.Visibility = isRemoteCustom ? Visibility.Visible : Visibility.Collapsed;
            BaseUrlBox.Visibility = isRemoteCustom ? Visibility.Visible : Visibility.Collapsed;

            if (providerId == "deepseek")
            {
                BaseUrlBox.Text = "https://api.deepseek.com";
                ModelCombo.Items.Clear();
                ModelCombo.Items.Add(new ComboBoxItem { Content = "deepseek-v4-flash", IsSelected = true });
                ModelCombo.Items.Add(new ComboBoxItem { Content = "deepseek-v4-pro" });
                ModelCombo.Text = "deepseek-v4-flash";
            }
            else if (providerId == "ollama")
            {
                BaseUrlBox.Text = "http://127.0.0.1:11434/v1";
                ModelCombo.Items.Clear();
                ModelCombo.Text = string.Empty;
            }
            else if (providerId == "lmstudio")
            {
                BaseUrlBox.Text = "http://127.0.0.1:1234/v1";
                ModelCombo.Items.Clear();
                ModelCombo.Text = string.Empty;
            }
            else
            {
                ModelCombo.Items.Clear();
                ModelCombo.Text = string.Empty;
            }
        }
        finally
        {
            _loadingProvider = false;
        }
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        FinishButton.IsEnabled = false;
        StatusText.Text = "正在连接 AI…";
        try
        {
            ApplyScene();

            var providerId = SelectedProvider();
            var preset = ModelProviderPresets.Find(providerId);
            var model = ModelCombo.Text.Trim();
            var apiKey = ApiKeyBox.Password.Trim();
            var baseUrl = providerId switch
            {
                "deepseek" => "https://api.deepseek.com",
                "ollama" => "http://127.0.0.1:11434/v1",
                "lmstudio" => "http://127.0.0.1:1234/v1",
                _ => BaseUrlBox.Text.Trim()
            };

            if (providerId == "deepseek" && string.IsNullOrWhiteSpace(apiKey))
            {
                StatusText.Text = "粘贴 DeepSeek API Key 就可以了；如果暂时没有，可以点“稍后连接 AI”。";
                return;
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                StatusText.Text = "请输入要使用的模型名称。";
                return;
            }
            if (providerId == "openai-compatible" && string.IsNullOrWhiteSpace(baseUrl))
            {
                StatusText.Text = "请输入 API 的 Base URL。";
                return;
            }

            var settings = new ModelSettings(providerId, preset.Mode, baseUrl, model, !string.IsNullOrWhiteSpace(apiKey));
            await _modelStore.SaveAsync(settings, apiKey);
            var applied = await _runtime.ConfigureModelAsync(settings, apiKey);
            if (applied is null)
            {
                StatusText.Text = "AI 暂时没有连通。请检查 Key / 本地模型是否已启动；桌面可以先正常使用。";
                return;
            }

            _onboardingStore.Complete();
            StatusText.Text = "完成。以后按 Alt + Space 直接叫出 AI。";
            DialogResult = true;
            Close();
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }

    private void SkipAi_Click(object sender, RoutedEventArgs e)
    {
        ApplyScene();
        _onboardingStore.Complete();
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        ApplyScene();
        _onboardingStore.Complete();
        DialogResult = true;
        Close();
    }

    private void ApplyScene()
    {
        var settings = _shellStore.Load();
        settings.Appearance.SceneId = _selectedSceneId;
        settings.Appearance.SceneMotionEnabled = true;
        settings.Appearance.AgentOrbEnabled = true;
        settings.Appearance.AgentCardsEnabled = true;
        _shellStore.Save(settings);
    }

    private string SelectedProvider() =>
        (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "deepseek";

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
