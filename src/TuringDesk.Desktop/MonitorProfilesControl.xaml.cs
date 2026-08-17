using System.Windows;
using System.Windows.Controls;
using TuringDesk.Desktop.Services;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class MonitorProfilesControl : UserControl
{
    private readonly SceneCatalogService _catalog = new();
    private readonly DesktopPlaybackSettingsStore _store = new();
    private DesktopPlaybackSettings _settings = new();
    private readonly Dictionary<string, AssignmentEditor> _editors = new(StringComparer.OrdinalIgnoreCase);

    public MonitorProfilesControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload(string? selectId = null)
    {
        _settings = _store.Load();
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _settings.Profiles;
        if (!string.IsNullOrWhiteSpace(selectId))
            ProfileList.SelectedItem = _settings.Profiles.FirstOrDefault(profile => profile.Id == selectId);
        else if (ProfileList.SelectedItem is null && _settings.Profiles.Count > 0)
            ProfileList.SelectedIndex = 0;
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadProfileEditor(ProfileList.SelectedItem as DesktopProfile);
    }

    private void LoadProfileEditor(DesktopProfile? profile)
    {
        ProfileNameBox.Text = profile?.Name ?? string.Empty;
        AssignmentPanel.Children.Clear();
        _editors.Clear();
        if (profile is null) return;

        var scenes = _catalog.LoadAll().Select(scene => new Choice(scene.Title, scene.Id)).ToArray();
        var playlists = _settings.Playlists.Select(list => new Choice(list.Name, list.Id)).ToArray();

        foreach (var monitor in DisplayManager.GetMonitors())
        {
            var key = monitor.IsPrimary ? "primary" : monitor.Id;
            var assignment = profile.Monitors.FirstOrDefault(item =>
                string.Equals(item.MonitorKey, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.MonitorKey, monitor.Id, StringComparison.OrdinalIgnoreCase));

            var card = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 21, 31)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 53, 72)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = monitor.IsPrimary ? $"主显示器 · {monitor.Width}×{monitor.Height}" : $"显示器 · {monitor.Width}×{monitor.Height}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 9)
            };
            Grid.SetColumnSpan(title, 4);
            grid.Children.Add(title);

            var mode = new ComboBox { Height = 32 };
            mode.Items.Add(new ComboBoxItem { Tag = "scene", Content = "单个桌面" });
            mode.Items.Add(new ComboBoxItem { Tag = "playlist", Content = "播放列表" });
            Grid.SetRow(mode, 1);
            Grid.SetColumn(mode, 1);
            grid.Children.Add(mode);

            var target = new ComboBox { Height = 32, DisplayMemberPath = nameof(Choice.Title), SelectedValuePath = nameof(Choice.Id) };
            Grid.SetRow(target, 1);
            Grid.SetColumn(target, 3);
            grid.Children.Add(target);

            var label = new TextBlock { Text = "内容", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 154, 175)), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var editor = new AssignmentEditor(monitor, key, mode, target, scenes, playlists);
            _editors[key] = editor;
            mode.SelectionChanged += (_, _) => editor.RefreshTargets();

            var playlistMode = !string.IsNullOrWhiteSpace(assignment?.PlaylistId);
            mode.SelectedIndex = playlistMode ? 1 : 0;
            editor.RefreshTargets();
            target.SelectedValue = playlistMode ? assignment?.PlaylistId : assignment?.SceneId;
            if (target.SelectedIndex < 0 && target.Items.Count > 0) target.SelectedIndex = 0;

            card.Child = grid;
            AssignmentPanel.Children.Add(card);
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new DesktopProfile { Name = $"多屏配置 {_settings.Profiles.Count + 1}" };
        foreach (var monitor in DisplayManager.GetMonitors())
        {
            profile.Monitors.Add(new MonitorSceneAssignment
            {
                MonitorKey = monitor.IsPrimary ? "primary" : monitor.Id,
                SceneId = "builtin:aurora"
            });
        }
        _settings.Profiles.Add(profile);
        _store.Save(_settings);
        Reload(profile.Id);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not DesktopProfile profile) return;
        _settings.Profiles.RemoveAll(item => item.Id == profile.Id);
        if (_settings.ActiveProfileId == profile.Id) _settings.ActiveProfileId = null;
        _store.Save(_settings);
        Reload();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not DesktopProfile profile) return;
        SaveEditorToProfile(profile);
        _store.Save(_settings);
        Reload(profile.Id);
        ShellNotificationService.Publish("多屏配置已保存", profile.Name, "shell");
    }

    private void ActivateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not DesktopProfile profile) return;
        SaveEditorToProfile(profile);
        _settings.ActiveProfileId = profile.Id;
        _settings.ActivePlaylistId = null;
        _store.Save(_settings);
        ShellNotificationService.Publish("多屏配置已应用", profile.Name, "shell");
    }

    private void SaveEditorToProfile(DesktopProfile profile)
    {
        profile.Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "多屏配置" : ProfileNameBox.Text.Trim();
        profile.Monitors.Clear();
        foreach (var editor in _editors.Values)
        {
            var mode = editor.Mode;
            var target = editor.SelectedTargetId;
            if (string.IsNullOrWhiteSpace(target)) continue;
            profile.Monitors.Add(new MonitorSceneAssignment
            {
                MonitorKey = editor.MonitorKey,
                SceneId = mode == "scene" ? target : null,
                PlaylistId = mode == "playlist" ? target : null
            });
        }
    }

    private sealed record Choice(string Title, string Id);

    private sealed class AssignmentEditor(
        DisplayMonitor monitor,
        string monitorKey,
        ComboBox modeCombo,
        ComboBox targetCombo,
        Choice[] scenes,
        Choice[] playlists)
    {
        public DisplayMonitor Monitor { get; } = monitor;
        public string MonitorKey { get; } = monitorKey;
        public string Mode => (modeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "scene";
        public string? SelectedTargetId => targetCombo.SelectedValue?.ToString();

        public void RefreshTargets()
        {
            var previous = targetCombo.SelectedValue?.ToString();
            targetCombo.ItemsSource = Mode == "playlist" ? playlists : scenes;
            if (!string.IsNullOrWhiteSpace(previous)) targetCombo.SelectedValue = previous;
            if (targetCombo.SelectedIndex < 0 && targetCombo.Items.Count > 0) targetCombo.SelectedIndex = 0;
        }
    }
}
