using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuringDesk.Desktop.Services;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class DesktopLibraryWindow : Window
{
    private readonly RuntimeClient _runtime;
    private readonly ModelSettingsStore _modelStore;
    private readonly ShellSettingsStore _shellStore = new();
    private readonly SceneCatalogService _sceneCatalog = new();
    private readonly DesktopPlaybackSettingsStore _playbackStore = new();
    private List<SceneManifest> _allScenes = [];
    private DesktopPlaybackSettings _playback = new();

    public DesktopLibraryWindow(RuntimeClient runtime, ModelSettingsStore modelStore)
    {
        _runtime = runtime;
        _modelStore = modelStore;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            ReloadScenes();
            ReloadPlayback();
            await RefreshAiStatusAsync();
        };
    }

    private void ReloadScenes()
    {
        _allScenes = _sceneCatalog.LoadAll().ToList();
        ApplySceneFilter();
        var currentId = _shellStore.Load().Appearance.SceneId;
        CurrentSceneText.Text = _sceneCatalog.Find(currentId)?.Title ?? currentId;
    }

    private void ApplySceneFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        SceneList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _allScenes
            : _allScenes.Where(scene =>
                scene.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (scene.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                scene.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    private void ReloadPlayback()
    {
        _playback = _playbackStore.Load();
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = _playback.Playlists;
        RuleGrid.ItemsSource = null;
        RuleGrid.ItemsSource = _playback.ApplicationRules;
        SelectEnumTag(FullscreenBehaviorCombo, _playback.FullscreenBehavior.ToString());
        SelectEnumTag(MaximizedBehaviorCombo, _playback.MaximizedBehavior.ToString());
        FpsSlider.Value = _playback.GlobalFpsLimit;
        VolumeSlider.Value = _playback.GlobalVolume;
        FpsText.Text = $"{_playback.GlobalFpsLimit} FPS";
        VolumeText.Text = _playback.GlobalVolume <= 0.001 ? "静音" : $"{_playback.GlobalVolume:P0}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplySceneFilter();
    }

    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneManifest scene)
        {
            ApplySceneButton.IsEnabled = false;
            ExportSceneButton.IsEnabled = false;
            return;
        }

        SelectedTitle.Text = scene.Title;
        SelectedMeta.Text = $"{scene.Kind} · {scene.Author ?? "未知作者"} · {scene.PreferredFps} FPS";
        SelectedDescription.Text = scene.Description ?? (scene.IsBuiltIn ? "TuringDesk 内置场景" : "用户导入桌面");
        ApplySceneButton.IsEnabled = true;
        ExportSceneButton.IsEnabled = !scene.IsBuiltIn;
    }

    private void ApplyScene_Click(object sender, RoutedEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneManifest scene) return;

        // A direct scene selection must win over a previously active playlist or
        // multi-monitor profile. Persist those overrides first so the wallpaper
        // host sees the cleared policy when ShellSettingsChanged fires. The host
        // also polls these files once per second, so this remains deterministic
        // when the Library is opened from a second TuringDesk process.
        _playback.ActivePlaylistId = null;
        _playback.ActiveProfileId = null;
        _playbackStore.Save(_playback);

        var shell = _shellStore.Load();
        shell.Appearance.SceneId = scene.Id;
        _shellStore.Save(shell);

        CurrentSceneText.Text = scene.Title;
        ShellNotificationService.Publish("桌面场景已应用", $"{scene.Title} · 最迟 1 秒同步到桌面", "shell");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入桌面",
            Filter = "支持的桌面|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.webm;*.mov;*.m4v;*.avi;*.html;*.htm;*.tdscene|图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|视频|*.mp4;*.webm;*.mov;*.m4v;*.avi|Web|*.html;*.htm|TuringDesk 场景包|*.tdscene|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        ImportPath(dialog.FileName);
    }

    private void ImportPath(string path)
    {
        try
        {
            var scene = _sceneCatalog.Import(path);
            ReloadScenes();
            SceneList.SelectedItem = _allScenes.FirstOrDefault(item => item.Id == scene.Id);
            ShellNotificationService.Publish("已导入桌面", scene.Title, "shell");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "无法导入桌面", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportScene_Click(object sender, RoutedEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneManifest scene || scene.IsBuiltIn) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出 TuringDesk 场景包",
            FileName = scene.Title + ".tdscene",
            Filter = "TuringDesk Scene|*.tdscene"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _sceneCatalog.Export(scene.Id, dialog.FileName);
            ShellNotificationService.Publish("场景已导出", dialog.FileName, "shell");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        foreach (var file in files) ImportPath(file);
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var playlist = new ScenePlaylist { Name = $"播放列表 {_playback.Playlists.Count + 1}" };
        _playback.Playlists.Add(playlist);
        _playbackStore.Save(_playback);
        ReloadPlayback();
        PlaylistList.SelectedItem = _playback.Playlists.FirstOrDefault(item => item.Id == playlist.Id);
    }

    private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not ScenePlaylist playlist) return;
        _playback.Playlists.RemoveAll(item => item.Id == playlist.Id);
        if (_playback.ActivePlaylistId == playlist.Id) _playback.ActivePlaylistId = null;
        _playbackStore.Save(_playback);
        ReloadPlayback();
        ClearPlaylistEditor();
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not ScenePlaylist playlist)
        {
            ClearPlaylistEditor();
            return;
        }
        PlaylistNameBox.Text = playlist.Name;
        PlaylistScenesBox.Text = string.Join(Environment.NewLine, playlist.SceneIds);
        PlaylistIntervalBox.Text = playlist.IntervalMinutes.ToString();
        PlaylistShuffleCheck.IsChecked = playlist.Shuffle;
        PlaylistChangePausedCheck.IsChecked = playlist.ChangeWhilePaused;
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not ScenePlaylist playlist) return;
        playlist.Name = string.IsNullOrWhiteSpace(PlaylistNameBox.Text) ? "播放列表" : PlaylistNameBox.Text.Trim();
        playlist.SceneIds = PlaylistScenesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => _sceneCatalog.Find(id) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        playlist.IntervalMinutes = int.TryParse(PlaylistIntervalBox.Text, out var minutes) ? Math.Clamp(minutes, 1, 1440) : 10;
        playlist.Shuffle = PlaylistShuffleCheck.IsChecked == true;
        playlist.ChangeWhilePaused = PlaylistChangePausedCheck.IsChecked == true;
        _playbackStore.Save(_playback);
        ReloadPlayback();
    }

    private void ActivatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not ScenePlaylist playlist || playlist.SceneIds.Count == 0) return;
        SavePlaylist_Click(sender, e);
        _playback.ActivePlaylistId = playlist.Id;
        _playback.ActiveProfileId = null;
        _playbackStore.Save(_playback);
        ShellNotificationService.Publish("播放列表已启用", playlist.Name, "shell");
    }

    private void ClearPlaylistEditor()
    {
        PlaylistNameBox.Text = string.Empty;
        PlaylistScenesBox.Text = string.Empty;
        PlaylistIntervalBox.Text = "10";
        PlaylistShuffleCheck.IsChecked = false;
        PlaylistChangePausedCheck.IsChecked = false;
    }

    private void RuleActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var action = SelectedTag(RuleActionCombo, "Pause");
        var needsTarget = action is "LoadScene" or "LoadPlaylist" or "LoadProfile";
        RuleTargetLabel.Visibility = needsTarget ? Visibility.Visible : Visibility.Collapsed;
        RuleTargetBox.Visibility = needsTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var exe = RuleExeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(exe)) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        var condition = Enum.TryParse<ApplicationRuleCondition>(SelectedTag(RuleConditionCombo, "Running"), out var c) ? c : ApplicationRuleCondition.Running;
        var action = Enum.TryParse<ApplicationRuleAction>(SelectedTag(RuleActionCombo, "Pause"), out var a) ? a : ApplicationRuleAction.Pause;
        var target = action is ApplicationRuleAction.LoadScene or ApplicationRuleAction.LoadPlaylist or ApplicationRuleAction.LoadProfile
            ? RuleTargetBox.Text.Trim()
            : null;
        if (target is not null && string.IsNullOrWhiteSpace(target)) return;

        _playback.ApplicationRules.RemoveAll(rule => string.Equals(rule.ExeName, exe, StringComparison.OrdinalIgnoreCase) && rule.Condition == condition);
        _playback.ApplicationRules.Add(new ApplicationPlaybackRule { ExeName = exe, Condition = condition, Action = action, TargetId = target });
        _playbackStore.Save(_playback);
        ReloadPlayback();
        RuleExeBox.Clear();
        RuleTargetBox.Clear();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not ApplicationPlaybackRule rule) return;
        _playback.ApplicationRules.Remove(rule);
        _playbackStore.Save(_playback);
        ReloadPlayback();
    }

    private void SavePerformance_Click(object sender, RoutedEventArgs e)
    {
        _playback.FullscreenBehavior = Enum.TryParse<PlaybackBehavior>(SelectedTag(FullscreenBehaviorCombo, "Pause"), out var full) ? full : PlaybackBehavior.Pause;
        _playback.MaximizedBehavior = Enum.TryParse<PlaybackBehavior>(SelectedTag(MaximizedBehaviorCombo, "KeepRunning"), out var max) ? max : PlaybackBehavior.KeepRunning;
        _playback.GlobalFpsLimit = (int)Math.Round(FpsSlider.Value);
        _playback.GlobalVolume = VolumeSlider.Value;
        _playbackStore.Save(_playback);
        FpsText.Text = $"{_playback.GlobalFpsLimit} FPS";
        VolumeText.Text = _playback.GlobalVolume <= 0.001 ? "静音" : $"{_playback.GlobalVolume:P0}";
        ShellNotificationService.Publish("性能设置已保存", "全屏与应用规则会自动生效。", "shell");
    }

    private async void ModelSettings_Click(object sender, RoutedEventArgs e)
    {
        var current = await _modelStore.LoadAsync();
        var key = _modelStore.LoadApiKey();
        var dialog = new ModelSettingsWindow(_runtime, _modelStore, current, key) { Owner = this };
        dialog.ShowDialog();
        await RefreshAiStatusAsync();
    }

    private async Task RefreshAiStatusAsync()
    {
        var settings = await _modelStore.LoadAsync();
        if (settings.ProviderId == "mock")
        {
            AiModelText.Text = "尚未连接真实模型";
            return;
        }
        var preset = ModelProviderPresets.Find(settings.ProviderId);
        AiModelText.Text = $"{preset.Name} · {settings.Model}";
    }

    private void Harness_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HarnessConsoleWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectEnumTag(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)) continue;
            combo.SelectedItem = item;
            return;
        }
        combo.SelectedIndex = 0;
    }
}
