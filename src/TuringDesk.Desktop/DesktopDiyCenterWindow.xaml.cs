using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopDiyCenterWindow : Window
{
    private readonly RuntimeClient _runtime;
    private readonly ModelSettingsStore _modelStore;
    private readonly ShellSettingsStore _settingsStore = new();
    private ShellSettings _settings;
    private bool _loading;

    public DesktopDiyCenterWindow(RuntimeClient runtime, ModelSettingsStore modelStore)
    {
        _runtime = runtime;
        _modelStore = modelStore;
        _settings = _settingsStore.Load();

        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadControlsFromSettings();
            RefreshPreview();
        };
    }

    private void LoadControlsFromSettings()
    {
        _loading = true;
        try
        {
            var appearance = _settings.Appearance;
            SelectByTag(WallpaperModeCombo, appearance.WallpaperMode);
            WallpaperPathBox.Text = appearance.WallpaperPath ?? string.Empty;
            SelectByTag(WallpaperFitCombo, appearance.WallpaperFit);
            AccentBox.Text = appearance.AccentHex;
            TaskbarOpacitySlider.Value = appearance.TaskbarOpacity;
            AgentCardsEnabledCheck.IsChecked = appearance.AgentCardsEnabled;
            AgentCardOpacitySlider.Value = appearance.AgentCardOpacity;
            AgentAutoHideSlider.Value = appearance.AgentCardAutoHideSeconds;
            SelectByTag(AgentSideCombo, appearance.AgentCardSide);
        }
        finally
        {
            _loading = false;
        }
    }

    private void AppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;

        var appearance = _settings.Appearance;
        appearance.WallpaperMode = SelectedTag(WallpaperModeCombo, "system");
        appearance.WallpaperPath = string.IsNullOrWhiteSpace(WallpaperPathBox.Text) ? null : WallpaperPathBox.Text.Trim();
        appearance.WallpaperFit = SelectedTag(WallpaperFitCombo, "cover");
        appearance.AccentHex = string.IsNullOrWhiteSpace(AccentBox.Text) ? "#8796FF" : AccentBox.Text.Trim();
        appearance.TaskbarOpacity = TaskbarOpacitySlider.Value;
        appearance.AgentCardsEnabled = AgentCardsEnabledCheck.IsChecked == true;
        appearance.AgentCardOpacity = AgentCardOpacitySlider.Value;
        appearance.AgentCardAutoHideSeconds = (int)Math.Round(AgentAutoHideSlider.Value);
        appearance.AgentCardSide = SelectedTag(AgentSideCombo, "right");

        _settingsStore.Save(_settings);
        _settings = _settingsStore.Load();
        ShellThemeService.Apply(_settings.Appearance);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!IsLoaded) return;
        var appearance = _settings.Appearance;
        PreviewWallpaper.Background = WallpaperService.CreateWallpaperBrush(appearance)
            ?? new SolidColorBrush(Color.FromRgb(18, 23, 34));
        PreviewTaskbar.Opacity = appearance.TaskbarOpacity;
        PreviewConversationCard.Opacity = appearance.AgentCardOpacity;
        PreviewTraceCard.Opacity = appearance.AgentCardOpacity;
        PreviewConversationCard.Visibility = appearance.AgentCardsEnabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewTraceCard.Visibility = appearance.AgentCardsEnabled ? Visibility.Visible : Visibility.Collapsed;
        PreviewConversationCard.BorderBrush = ShellThemeService.AccentBrush(appearance);
    }

    private void Model_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HarnessConsoleWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset }) return;

        var appearance = _settings.Appearance;
        switch (preset)
        {
            case "deep":
                appearance.WallpaperMode = "system";
                appearance.AccentHex = "#8C9CFF";
                appearance.TaskbarOpacity = 0.92;
                appearance.AgentCardOpacity = 0.96;
                appearance.AgentCardsEnabled = true;
                break;
            case "graphite":
                appearance.WallpaperMode = "solid";
                appearance.AccentHex = "#A8B0BE";
                appearance.TaskbarOpacity = 0.98;
                appearance.AgentCardOpacity = 0.94;
                appearance.AgentCardsEnabled = true;
                break;
            default:
                appearance.WallpaperMode = "system";
                appearance.AccentHex = "#8796FF";
                appearance.TaskbarOpacity = 0.96;
                appearance.AgentCardOpacity = 0.96;
                appearance.AgentCardsEnabled = true;
                break;
        }

        _settingsStore.Save(_settings);
        _settings = _settingsStore.Load();
        ShellThemeService.Apply(_settings.Appearance);
        LoadControlsFromSettings();
        RefreshPreview();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settingsStore.ResetAppearance();
        _settings = _settingsStore.Load();
        ShellThemeService.Apply(_settings.Appearance);
        LoadControlsFromSettings();
        RefreshPreview();
    }

    private void BrowseWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 TuringDesk 桌面壁纸",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;
        _loading = true;
        WallpaperModeCombo.SelectedIndex = 1;
        WallpaperPathBox.Text = dialog.FileName;
        _loading = false;
        AppearanceChanged(sender, e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) continue;
            combo.SelectedItem = item;
            return;
        }
        combo.SelectedIndex = 0;
    }
}
