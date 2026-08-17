using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private readonly Dictionary<string, FrameworkElement> _sceneGraphElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SceneLayerDefinition> _sceneGraphLayers = new(StringComparer.OrdinalIgnoreCase);

    private bool LoadSceneGraph(SceneManifest scene)
    {
        if (scene.Layers.Count == 0) return false;

        SceneGraphCanvas.Children.Clear();
        _sceneGraphElements.Clear();
        _sceneGraphLayers.Clear();
        SceneGraphCanvas.Visibility = Visibility.Visible;
        BuiltInScene.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        WebPlayer.Visibility = Visibility.Collapsed;

        var width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        var height = Math.Max(1, SystemParameters.VirtualScreenHeight);

        foreach (var layer in scene.Layers.Where(layer => layer.Visible).OrderBy(layer => layer.Depth))
        {
            var element = BuildLayerElement(scene, layer, width, height);
            if (element is null) continue;

            ApplyLayerTransform(element, layer, width, height);
            ApplyLayerEffects(element, layer);
            Panel.SetZIndex(element, (int)Math.Round(layer.Depth * 1000));
            SceneGraphCanvas.Children.Add(element);
            _sceneGraphElements[layer.Id] = element;
            _sceneGraphLayers[layer.Id] = layer;
        }

        StartSceneTimeline(scene);
        return true;
    }

    private FrameworkElement? BuildLayerElement(SceneManifest scene, SceneLayerDefinition layer, double width, double height)
    {
        switch (layer.Kind)
        {
            case SceneLayerKind.Image:
                return BuildImageLayer(scene, layer);
            case SceneLayerKind.Text:
                return new TextBlock
                {
                    Text = layer.Text ?? layer.Name,
                    Foreground = Brushes.White,
                    FontSize = Math.Max(12, Math.Min(width, height) * 0.035),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                };
            case SceneLayerKind.Shape:
                return new Border
                {
                    Background = new SolidColorBrush(ParseColor(layer.Effects
                        .FirstOrDefault(effect => effect.Type.Equals("fill", StringComparison.OrdinalIgnoreCase))?
                        .Strings.GetValueOrDefault("color"), Color.FromArgb(130, 100, 119, 255))),
                    CornerRadius = new CornerRadius(24)
                };
            case SceneLayerKind.Particle:
                return BuildParticleLayer(layer, width, height);
            case SceneLayerKind.Audio:
                return null;
            case SceneLayerKind.Model3D:
                // The scene contract already reserves 3D layers. The D3D renderer
                // will own mesh/material playback; WPF safely skips them for now.
                return null;
            default:
                return null;
        }
    }

    private FrameworkElement? BuildImageLayer(SceneManifest scene, SceneLayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Source) || string.IsNullOrWhiteSpace(scene.PackageRoot)) return null;
        var path = Path.GetFullPath(Path.Combine(scene.PackageRoot, layer.Source));
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return new Image
            {
                Source = image,
                Stretch = Stretch.UniformToFill
            };
        }
        catch (Exception error)
        {
            PlaybackError?.Invoke($"Layer '{layer.Name}' image failed: {error.Message}");
            return null;
        }
    }

    private FrameworkElement BuildParticleLayer(SceneLayerDefinition layer, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height };
        var settings = layer.Particle ?? new SceneParticleDefinition();
        var count = Math.Clamp(settings.MaxParticles, 1, 512);
        var color = ParseColor(settings.Color, Color.FromRgb(221, 230, 255));

        for (var i = 0; i < count; i++)
        {
            var size = settings.MinSize + _random.NextDouble() * Math.Max(0.1, settings.MaxSize - settings.MinSize);
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(color),
                Opacity = 0.2 + _random.NextDouble() * 0.7
            };
            Canvas.SetLeft(dot, _random.NextDouble() * width);
            Canvas.SetTop(dot, _random.NextDouble() * height);
            canvas.Children.Add(dot);

            var distance = settings.MinSpeed + _random.NextDouble() * Math.Max(1, settings.MaxSpeed - settings.MinSpeed);
            var seconds = Math.Max(1, settings.LifetimeSeconds * (0.65 + _random.NextDouble() * 0.7));
            dot.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(Canvas.GetTop(dot), Canvas.GetTop(dot) - distance * seconds, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        return canvas;
    }

    private static void ApplyLayerTransform(FrameworkElement element, SceneLayerDefinition layer, double width, double height)
    {
        var requestedWidth = layer.Width <= 1 ? width * Math.Clamp(layer.Width, 0.001, 1) : layer.Width;
        var requestedHeight = layer.Height <= 1 ? height * Math.Clamp(layer.Height, 0.001, 1) : layer.Height;
        element.Width = Math.Max(1, requestedWidth);
        element.Height = Math.Max(1, requestedHeight);
        element.Opacity = Math.Clamp(layer.Opacity, 0, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(layer.Scale, layer.Scale));
        group.Children.Add(new RotateTransform(layer.Rotation));
        group.Children.Add(new TranslateTransform());
        element.RenderTransform = group;

        var x = layer.X <= 1 ? width * layer.X : layer.X;
        var y = layer.Y <= 1 ? height * layer.Y : layer.Y;
        Canvas.SetLeft(element, x - element.Width / 2);
        Canvas.SetTop(element, y - element.Height / 2);
    }

    private static void ApplyLayerEffects(FrameworkElement element, SceneLayerDefinition layer)
    {
        var blur = layer.Effects.FirstOrDefault(effect => effect.Enabled && effect.Type.Equals("blur", StringComparison.OrdinalIgnoreCase));
        var glow = layer.Effects.FirstOrDefault(effect => effect.Enabled && effect.Type.Equals("glow", StringComparison.OrdinalIgnoreCase));
        if (glow is not null)
        {
            element.Effect = new DropShadowEffect
            {
                BlurRadius = glow.Numbers.GetValueOrDefault("radius", 18),
                ShadowDepth = 0,
                Opacity = Math.Clamp(glow.Numbers.GetValueOrDefault("opacity", 0.7), 0, 1),
                Color = ParseColor(glow.Strings.GetValueOrDefault("color"), Color.FromRgb(110, 125, 255))
            };
        }
        else if (blur is not null)
        {
            element.Effect = new BlurEffect { Radius = Math.Clamp(blur.Numbers.GetValueOrDefault("radius", 8), 0, 100) };
        }
    }

    private void StartSceneTimeline(SceneManifest scene)
    {
        foreach (var track in scene.Timeline)
        {
            if (!_sceneGraphElements.TryGetValue(track.LayerId, out var element) || track.Keyframes.Count < 2) continue;
            var frames = track.Keyframes.OrderBy(frame => frame.TimeSeconds).ToArray();
            var durationSeconds = Math.Max(0.01, frames[^1].TimeSeconds);
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(durationSeconds),
                RepeatBehavior = track.Loop ? RepeatBehavior.Forever : new RepeatBehavior(1)
            };
            foreach (var frame in frames)
            {
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(frame.Value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(0, frame.TimeSeconds))), CreateEasing(frame.Easing)));
            }

            switch (track.Property.ToLowerInvariant())
            {
                case "opacity":
                    element.BeginAnimation(OpacityProperty, animation);
                    break;
                case "x":
                    element.BeginAnimation(Canvas.LeftProperty, animation);
                    break;
                case "y":
                    element.BeginAnimation(Canvas.TopProperty, animation);
                    break;
                case "rotation":
                    if (element.RenderTransform is TransformGroup group && group.Children.OfType<RotateTransform>().FirstOrDefault() is { } rotate)
                        rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
                    break;
                case "scale":
                    if (element.RenderTransform is TransformGroup transforms && transforms.Children.OfType<ScaleTransform>().FirstOrDefault() is { } scale)
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
                    }
                    break;
            }
        }
    }

    private void PauseSceneGraphAnimations()
    {
        foreach (var element in _sceneGraphElements.Values) element.BeginAnimation(OpacityProperty, null);
        foreach (var canvas in _sceneGraphElements.Values.OfType<Canvas>())
        {
            foreach (var dot in canvas.Children.OfType<Ellipse>()) dot.BeginAnimation(Canvas.TopProperty, null);
        }
    }

    private void ResumeSceneGraphAnimations()
    {
        if (_scene is not null) StartSceneTimeline(_scene);
    }

    private void StopSceneGraph()
    {
        PauseSceneGraphAnimations();
        SceneGraphCanvas.Children.Clear();
        SceneGraphCanvas.Visibility = Visibility.Collapsed;
        _sceneGraphElements.Clear();
        _sceneGraphLayers.Clear();
    }

    private void ApplySceneGraphParallax(DesktopInputSnapshot input)
    {
        foreach (var pair in _sceneGraphElements)
        {
            if (!_sceneGraphLayers.TryGetValue(pair.Key, out var layer) || !layer.MouseParallax) continue;
            if (pair.Value.RenderTransform is not TransformGroup group) continue;
            var translate = group.Children.OfType<TranslateTransform>().LastOrDefault();
            if (translate is null) continue;
            var strength = Math.Clamp(layer.ParallaxStrength, -1, 1);
            translate.X = (input.NormalizedX - 0.5) * -SystemParameters.VirtualScreenWidth * strength;
            translate.Y = (input.NormalizedY - 0.5) * -SystemParameters.VirtualScreenHeight * strength;
        }
    }

    private static IEasingFunction? CreateEasing(string? easing) => easing?.ToLowerInvariant() switch
    {
        "easein" => new QuadraticEase { EasingMode = EasingMode.EaseIn },
        "easeout" => new QuadraticEase { EasingMode = EasingMode.EaseOut },
        "easeinout" => new SineEase { EasingMode = EasingMode.EaseInOut },
        _ => null
    };

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }
}
