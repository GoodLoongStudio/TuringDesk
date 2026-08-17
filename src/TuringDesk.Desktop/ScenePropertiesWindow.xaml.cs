using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class ScenePropertiesWindow : Window
{
    private readonly SceneManifest _scene;
    private readonly SceneInstanceSettingsStore _store = new();
    private SceneInstanceSettings _settings;
    private readonly Dictionary<string, FrameworkElement> _editors = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public ScenePropertiesWindow(SceneManifest scene)
    {
        _scene = scene;
        _settings = _store.Load(scene);
        InitializeComponent();
        Loaded += (_, _) => BuildUi();
    }

    private void BuildUi()
    {
        _loading = true;
        try
        {
            TitleText.Text = _scene.Title;
            KindText.Text = $"{_scene.Kind} · {_scene.Author ?? "未知作者"}";
            FpsSlider.Value = _settings.FpsLimit;
            FpsValueText.Text = $"{_settings.FpsLimit} FPS";
            MutedCheck.IsChecked = _settings.Muted;
            VolumeSlider.Value = _settings.Volume;
            VolumeSlider.IsEnabled = !_settings.Muted;
            VolumeValueText.Text = _settings.Muted ? "静音" : $"{_settings.Volume:P0}";

            PropertyPanel.Children.Clear();
            _editors.Clear();
            EmptyText.Visibility = _scene.Properties.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var group in _scene.Properties
                         .GroupBy(property => string.IsNullOrWhiteSpace(property.Group) ? "外观与行为" : property.Group!))
            {
                var section = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 23, 32)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(44, 54, 74)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(13),
                    Padding = new Thickness(15),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
                foreach (var definition in group)
                {
                    stack.Children.Add(BuildPropertyEditor(definition));
                }
                section.Child = stack;
                PropertyPanel.Children.Add(section);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private FrameworkElement BuildPropertyEditor(ScenePropertyDefinition definition)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        container.Children.Add(new TextBlock
        {
            Text = definition.Label,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(151, 162, 181)),
            FontSize = 10.5
        });

        _settings.Properties.TryGetValue(definition.Key, out var value);
        FrameworkElement editor;
        switch (definition.Kind)
        {
            case ScenePropertyKind.Bool:
            {
                var check = new CheckBox { IsChecked = ToBool(value), Margin = new Thickness(0, 6, 0, 0), Content = "启用" };
                editor = check;
                break;
            }
            case ScenePropertyKind.Slider:
            {
                var min = definition.Min ?? 0;
                var max = definition.Max ?? 1;
                if (max <= min) max = min + 1;
                var slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    Value = Math.Clamp(ToDouble(value, ToDouble(definition.Default, min)), min, max),
                    TickFrequency = definition.Step ?? Math.Max(0.01, (max - min) / 100),
                    IsSnapToTickEnabled = definition.Step is not null,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var valueText = new TextBlock { Text = slider.Value.ToString("0.###", CultureInfo.InvariantCulture), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(126, 138, 157)), FontSize = 9.5, Margin = new Thickness(0, 2, 0, 0) };
                slider.ValueChanged += (_, _) => valueText.Text = slider.Value.ToString("0.###", CultureInfo.InvariantCulture);
                container.Children.Add(slider);
                container.Children.Add(valueText);
                _editors[definition.Key] = slider;
                return container;
            }
            case ScenePropertyKind.Combo:
            {
                var combo = new ComboBox { Margin = new Thickness(0, 6, 0, 0), DisplayMemberPath = nameof(ScenePropertyOption.Label), SelectedValuePath = nameof(ScenePropertyOption.Value) };
                combo.ItemsSource = definition.Options;
                combo.SelectedValue = value?.ToString() ?? definition.Default?.ToString();
                if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
                editor = combo;
                break;
            }
            case ScenePropertyKind.File:
            {
                var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var box = new TextBox { Text = value?.ToString() ?? string.Empty, IsReadOnly = true, Padding = new Thickness(8, 6, 8, 6) };
                var button = new Button { Content = "浏览", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
                button.Click += (_, _) =>
                {
                    var dialog = new OpenFileDialog { Title = definition.Label, Filter = string.IsNullOrWhiteSpace(definition.FileFilter) ? "所有文件|*.*" : definition.FileFilter };
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
                };
                grid.Children.Add(box);
                Grid.SetColumn(button, 1);
                grid.Children.Add(button);
                container.Children.Add(grid);
                _editors[definition.Key] = box;
                return container;
            }
            case ScenePropertyKind.Shortcut:
            {
                var box = new TextBox { Text = value?.ToString() ?? definition.Default?.ToString() ?? string.Empty, IsReadOnly = true, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 6, 8, 6), ToolTip = "点击后按一个快捷键组合" };
                box.PreviewKeyDown += (_, e) =>
                {
                    e.Handled = true;
                    var modifiers = Keyboard.Modifiers;
                    if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                    var prefix = modifiers == ModifierKeys.None ? string.Empty : modifiers + "+";
                    box.Text = prefix + e.Key;
                };
                editor = box;
                break;
            }
            default:
            {
                var box = new TextBox
                {
                    Text = value?.ToString() ?? definition.Default?.ToString() ?? string.Empty,
                    Margin = new Thickness(0, 6, 0, 0),
                    Padding = new Thickness(8, 6, 8, 6),
                    ToolTip = definition.Kind == ScenePropertyKind.Color ? "#RRGGBB 或 #AARRGGBB" : null
                };
                editor = box;
                break;
            }
        }

        container.Children.Add(editor);
        _editors[definition.Key] = editor;
        return container;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ReadUiIntoSettings();
        _store.Save(_settings);
        StatusText.Text = "已保存，当前桌面已收到新属性。";
        DialogResult = true;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings = new SceneInstanceSettings
        {
            SceneId = _scene.Id,
            FpsLimit = _scene.PreferredFps,
            Muted = _scene.Muted,
            Volume = 0,
            Properties = new Dictionary<string, object?>(_scene.Defaults, StringComparer.OrdinalIgnoreCase)
        };
        foreach (var definition in _scene.Properties)
        {
            if (!_settings.Properties.ContainsKey(definition.Key)) _settings.Properties[definition.Key] = definition.Default;
        }
        BuildUi();
        StatusText.Text = "已恢复默认值；点击“保存并应用”确认。";
    }

    private void ReadUiIntoSettings()
    {
        _settings.FpsLimit = (int)Math.Round(FpsSlider.Value);
        _settings.Muted = MutedCheck.IsChecked == true;
        _settings.Volume = VolumeSlider.Value;
        foreach (var definition in _scene.Properties)
        {
            if (!_editors.TryGetValue(definition.Key, out var editor)) continue;
            _settings.Properties[definition.Key] = editor switch
            {
                CheckBox check => check.IsChecked == true,
                Slider slider => slider.Value,
                ComboBox combo => combo.SelectedValue?.ToString(),
                TextBox box => box.Text,
                _ => null
            };
        }
    }

    private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsValueText is not null) FpsValueText.Text = $"{(int)Math.Round(e.NewValue)} FPS";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText is null) return;
        VolumeValueText.Text = MutedCheck?.IsChecked == true ? "静音" : $"{e.NewValue:P0}";
    }

    private void MutedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider is null) return;
        VolumeSlider.IsEnabled = MutedCheck.IsChecked != true;
        VolumeValueText.Text = MutedCheck.IsChecked == true ? "静音" : $"{VolumeSlider.Value:P0}";
    }

    private static bool ToBool(object? value)
    {
        if (value is bool boolean) return boolean;
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static double ToDouble(object? value, double fallback)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonDouble)) return jsonDouble;
        if (value is double d) return d;
        if (value is float f) return f;
        if (value is int i) return i;
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
