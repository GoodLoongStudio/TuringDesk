using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuringDesk.Desktop.Services;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneEditorWindow : Window
{
    private readonly SceneCatalogService _catalog = new();
    private readonly ShellSettingsStore _shellStore = new();
    private SceneManifest _scene;
    private bool _loadingInspector;
    private int _previewVersion;

    public SceneEditorWindow(SceneManifest scene)
    {
        _scene = scene.IsBuiltIn ? _catalog.DuplicateForEditing(scene) : scene;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            TitleText.Text = _scene.Title;
            SceneIdText.Text = _scene.Id;
            ScriptPathBox.Text = _scene.Script ?? string.Empty;
            RefreshLayerList();
            await RefreshPreviewAsync();
        };
    }

    public SceneEditorWindow() : this(new SceneCatalogService().CreateNewScene())
    {
    }

    public SceneManifest Scene => _scene;

    private void RefreshLayerList(string? selectId = null)
    {
        var selected = selectId ?? (LayerList.SelectedItem as SceneLayerDefinition)?.Id;
        LayerList.ItemsSource = null;
        LayerList.ItemsSource = _scene.Layers;
        if (!string.IsNullOrWhiteSpace(selected))
            LayerList.SelectedItem = _scene.Layers.FirstOrDefault(layer => layer.Id == selected);
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        try
        {
            var appearance = _shellStore.Load().Appearance;
            await PreviewRenderer.LoadAsync(_scene, appearance);
            if (version == _previewVersion) StatusText.Text = "实时预览已更新";
        }
        catch (Exception error)
        {
            if (version == _previewVersion) StatusText.Text = "预览失败：" + error.Message;
        }
    }

    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadInspector(LayerList.SelectedItem as SceneLayerDefinition);
    }

    private void LoadInspector(SceneLayerDefinition? layer)
    {
        _loadingInspector = true;
        try
        {
            LayerInspector.IsEnabled = layer is not null;
            InspectorHint.Text = layer is null ? "选择一个图层" : $"{layer.Kind} · {layer.Name}";
            if (layer is null) return;

            LayerNameBox.Text = layer.Name;
            LayerXBox.Text = layer.X.ToString("0.###", CultureInfo.InvariantCulture);
            LayerYBox.Text = layer.Y.ToString("0.###", CultureInfo.InvariantCulture);
            LayerWidthBox.Text = layer.Width.ToString("0.###", CultureInfo.InvariantCulture);
            LayerHeightBox.Text = layer.Height.ToString("0.###", CultureInfo.InvariantCulture);
            LayerScaleBox.Text = layer.Scale.ToString("0.###", CultureInfo.InvariantCulture);
            LayerRotationBox.Text = layer.Rotation.ToString("0.###", CultureInfo.InvariantCulture);
            LayerOpacitySlider.Value = layer.Opacity;
            LayerParallaxCheck.IsChecked = layer.MouseParallax;
            LayerParallaxSlider.Value = layer.ParallaxStrength;

            var blur = FindEffect(layer, "blur");
            BlurCheck.IsChecked = blur?.Enabled == true;
            BlurRadiusSlider.Value = blur?.Numbers.GetValueOrDefault("radius", 8) ?? 8;
            var glow = FindEffect(layer, "glow");
            GlowCheck.IsChecked = glow?.Enabled == true;
            GlowRadiusSlider.Value = glow?.Numbers.GetValueOrDefault("radius", 18) ?? 18;
        }
        finally
        {
            _loadingInspector = false;
        }
    }

    private async void LayerProperty_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingInspector || !IsLoaded || LayerList.SelectedItem is not SceneLayerDefinition layer) return;

        layer.Name = string.IsNullOrWhiteSpace(LayerNameBox.Text) ? layer.Kind.ToString() : LayerNameBox.Text.Trim();
        layer.X = ParseDouble(LayerXBox.Text, layer.X);
        layer.Y = ParseDouble(LayerYBox.Text, layer.Y);
        layer.Width = Math.Max(0.001, ParseDouble(LayerWidthBox.Text, layer.Width));
        layer.Height = Math.Max(0.001, ParseDouble(LayerHeightBox.Text, layer.Height));
        layer.Scale = Math.Max(0.01, ParseDouble(LayerScaleBox.Text, layer.Scale));
        layer.Rotation = ParseDouble(LayerRotationBox.Text, layer.Rotation);
        layer.Opacity = LayerOpacitySlider.Value;
        layer.MouseParallax = LayerParallaxCheck.IsChecked == true;
        layer.ParallaxStrength = LayerParallaxSlider.Value;
        UpdateEffect(layer, "blur", BlurCheck.IsChecked == true, BlurRadiusSlider.Value);
        UpdateEffect(layer, "glow", GlowCheck.IsChecked == true, GlowRadiusSlider.Value);

        InspectorHint.Text = $"{layer.Kind} · {layer.Name}";
        RefreshLayerList(layer.Id);
        await RefreshPreviewAsync();
    }

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "图片图层", () => AddImageLayer());
        AddMenuItem(menu, "文字图层", () => AddTextLayer());
        AddMenuItem(menu, "形状图层", () => AddShapeLayer());
        AddMenuItem(menu, "粒子图层", () => AddParticleLayer());
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private async void AddImage_Click(object sender, RoutedEventArgs e) => await AddImageLayerAsync();
    private void AddText_Click(object sender, RoutedEventArgs e) => AddTextLayer();
    private void AddParticles_Click(object sender, RoutedEventArgs e) => AddParticleLayer();

    private void AddImageLayer() => _ = AddImageLayerAsync();

    private async Task AddImageLayerAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加图片图层",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var relative = CopyAsset(dialog.FileName, "assets");
        var layer = new SceneLayerDefinition
        {
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            Kind = SceneLayerKind.Image,
            Source = relative,
            Width = 0.6,
            Height = 0.6,
            X = 0.5,
            Y = 0.5
        };
        _scene.Layers.Add(layer);
        RefreshLayerList(layer.Id);
        await RefreshPreviewAsync();
    }

    private void AddTextLayer()
    {
        var layer = new SceneLayerDefinition
        {
            Name = "文字",
            Kind = SceneLayerKind.Text,
            Text = "TuringDesk",
            Width = 0.4,
            Height = 0.15,
            X = 0.5,
            Y = 0.5
        };
        _scene.Layers.Add(layer);
        RefreshLayerList(layer.Id);
        _ = RefreshPreviewAsync();
    }

    private void AddShapeLayer()
    {
        var layer = new SceneLayerDefinition
        {
            Name = "形状",
            Kind = SceneLayerKind.Shape,
            Width = 0.32,
            Height = 0.22,
            X = 0.5,
            Y = 0.5,
            Effects = [new SceneEffectDefinition { Type = "fill", Strings = new() { ["color"] = "#6677FF" } }]
        };
        _scene.Layers.Add(layer);
        RefreshLayerList(layer.Id);
        _ = RefreshPreviewAsync();
    }

    private void AddParticleLayer()
    {
        var layer = new SceneLayerDefinition
        {
            Name = "粒子",
            Kind = SceneLayerKind.Particle,
            Width = 1,
            Height = 1,
            X = 0.5,
            Y = 0.5,
            MouseParallax = true,
            ParallaxStrength = 0.01,
            Particle = new SceneParticleDefinition { MaxParticles = 96, SpawnRate = 12, LifetimeSeconds = 14 }
        };
        _scene.Layers.Add(layer);
        RefreshLayerList(layer.Id);
        _ = RefreshPreviewAsync();
    }

    private void DeleteLayer_Click(object sender, RoutedEventArgs e)
    {
        if (LayerList.SelectedItem is not SceneLayerDefinition layer) return;
        _scene.Layers.Remove(layer);
        _scene.Timeline.RemoveAll(track => track.LayerId == layer.Id);
        RefreshLayerList();
        _ = RefreshPreviewAsync();
    }

    private void LayerUp_Click(object sender, RoutedEventArgs e) => MoveLayer(-1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => MoveLayer(1);

    private void MoveLayer(int delta)
    {
        if (LayerList.SelectedItem is not SceneLayerDefinition layer) return;
        var index = _scene.Layers.IndexOf(layer);
        var next = Math.Clamp(index + delta, 0, _scene.Layers.Count - 1);
        if (next == index) return;
        _scene.Layers.RemoveAt(index);
        _scene.Layers.Insert(next, layer);
        for (var i = 0; i < _scene.Layers.Count; i++) _scene.Layers[i].Depth = i / 100.0;
        RefreshLayerList(layer.Id);
        _ = RefreshPreviewAsync();
    }

    private void AddTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (LayerList.SelectedItem is not SceneLayerDefinition layer) return;
        var property = (TimelinePropertyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "opacity";
        var from = ParseDouble(TimelineFromBox.Text, 0);
        var to = ParseDouble(TimelineToBox.Text, 1);
        var seconds = Math.Max(0.1, ParseDouble(TimelineSecondsBox.Text, 4));

        _scene.Timeline.RemoveAll(track => track.LayerId == layer.Id && track.Property.Equals(property, StringComparison.OrdinalIgnoreCase));
        _scene.Timeline.Add(new SceneTimelineTrack
        {
            LayerId = layer.Id,
            Property = property,
            Loop = true,
            Keyframes =
            [
                new SceneKeyframe { TimeSeconds = 0, Value = from, Easing = "easeInOut" },
                new SceneKeyframe { TimeSeconds = seconds, Value = to, Easing = "easeInOut" }
            ]
        });
        StatusText.Text = $"已添加 {property} 动画 · {seconds:0.##} 秒循环";
        _ = RefreshPreviewAsync();
    }

    private void ChooseScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择场景脚本", Filter = "JavaScript|*.js|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _scene.Script = CopyAsset(dialog.FileName, "scripts");
        ScriptPathBox.Text = _scene.Script;
        StatusText.Text = "脚本已加入场景包；SceneScript 执行器将在脚本运行层启用。";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveScene(apply: false);
    private void SaveApply_Click(object sender, RoutedEventArgs e) => SaveScene(apply: true);

    private void SaveScene(bool apply)
    {
        try
        {
            _catalog.Save(_scene);
            if (apply)
            {
                var shell = _shellStore.Load();
                shell.Appearance.SceneId = _scene.Id;
                _shellStore.Save(shell);
            }
            StatusText.Text = apply ? "已保存并应用到桌面" : "已保存";
            ShellNotificationService.Publish(apply ? "场景已保存并应用" : "场景已保存", _scene.Title, "shell");
        }
        catch (Exception error)
        {
            StatusText.Text = "保存失败：" + error.Message;
        }
    }

    private string CopyAsset(string source, string folder)
    {
        if (string.IsNullOrWhiteSpace(_scene.PackageRoot)) throw new InvalidOperationException("Scene package root is unavailable.");
        var targetDir = Path.Combine(_scene.PackageRoot, folder);
        Directory.CreateDirectory(targetDir);
        var name = Path.GetFileName(source);
        var target = Path.Combine(targetDir, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var counter = 2;
        while (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            name = $"{stem}-{counter++}{ext}";
            target = Path.Combine(targetDir, name);
        }
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, overwrite: true);
        return Path.Combine(folder, name).Replace('\\', '/');
    }

    private static void AddMenuItem(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static SceneEffectDefinition? FindEffect(SceneLayerDefinition layer, string type) =>
        layer.Effects.FirstOrDefault(effect => effect.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    private static void UpdateEffect(SceneLayerDefinition layer, string type, bool enabled, double radius)
    {
        var effect = FindEffect(layer, type);
        if (effect is null)
        {
            effect = new SceneEffectDefinition { Type = type };
            layer.Effects.Add(effect);
        }
        effect.Enabled = enabled;
        effect.Numbers["radius"] = radius;
        if (type == "glow")
        {
            effect.Numbers["opacity"] = 0.72;
            effect.Strings["color"] = "#7687FF";
        }
    }

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
