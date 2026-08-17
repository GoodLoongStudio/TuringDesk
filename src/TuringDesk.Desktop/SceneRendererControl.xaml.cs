using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TuringDesk.Desktop.Services;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl : UserControl
{
    private readonly Random _random = new();
    private SceneManifest? _scene;
    private bool _paused;
    private bool _stopped;

    public SceneRendererControl()
    {
        InitializeComponent();
    }

    public SceneManifest? CurrentScene => _scene;
    public bool IsPaused => _paused;
    public bool IsStopped => _stopped;

    public event Action<string>? PlaybackError;

    public async Task LoadAsync(SceneManifest scene, ShellAppearanceSettings appearance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Stop();
        _scene = scene;
        _paused = false;
        _stopped = false;
        Root.Visibility = Visibility.Visible;
        ApplyStaticBackground(appearance);

        switch (scene.Kind)
        {
            case SceneKind.Video:
                LoadVideo(scene);
                break;
            case SceneKind.Web:
                await LoadWebAsync(scene, cancellationToken);
                break;
            default:
                LoadScene(scene, appearance);
                break;
        }

        // Scene-specific FPS/volume/mute/user properties are independent from
        // the global desktop profile and survive scene switching/import/export.
        await ApplyUserPropertiesAsync();
    }

    public void Pause()
    {
        if (_scene is null || _paused || _stopped) return;
        _paused = true;
        switch (_scene.Kind)
        {
            case SceneKind.Video:
                VideoPlayer.Pause();
                break;
            case SceneKind.Web:
                _ = SetWebPlaybackStateAsync(paused: true);
                break;
            default:
                PauseBuiltInAnimations();
                PauseSceneGraphAnimations();
                break;
        }
    }

    public void Resume()
    {
        if (_scene is null || !_paused || _stopped) return;
        _paused = false;
        switch (_scene.Kind)
        {
            case SceneKind.Video:
                VideoPlayer.Play();
                break;
            case SceneKind.Web:
                _ = SetWebPlaybackStateAsync(paused: false);
                break;
            default:
                ResumeBuiltInAnimations();
                ResumeSceneGraphAnimations();
                break;
        }
    }

    public void Stop()
    {
        _paused = false;
        _stopped = true;
        StopBuiltInAnimations();
        StopSceneGraph();
        try { VideoPlayer.Stop(); } catch { }
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        if (WebPlayer.CoreWebView2 is not null)
        {
            try { WebPlayer.CoreWebView2.Navigate("about:blank"); } catch { }
        }
        WebPlayer.Visibility = Visibility.Collapsed;
        BuiltInScene.Visibility = Visibility.Collapsed;
        ParticleCanvas.Children.Clear();
        Root.Visibility = Visibility.Visible;
    }

    public void SetVolume(double volume, bool muted)
    {
        var normalized = Math.Clamp(volume, 0, 1);
        VideoPlayer.Volume = muted ? 0 : normalized;
        VideoPlayer.IsMuted = muted;
        if (_scene?.Kind == SceneKind.Web)
        {
            _ = SetWebVolumeAsync(muted ? 0 : normalized);
        }
    }

    private void ApplyStaticBackground(ShellAppearanceSettings appearance)
    {
        StaticBackground.Background = WallpaperService.CreateWallpaperBrush(appearance)
            ?? new SolidColorBrush(Color.FromRgb(8, 10, 16));
    }

    private void LoadScene(SceneManifest scene, ShellAppearanceSettings appearance)
    {
        VideoPlayer.Visibility = Visibility.Collapsed;
        WebPlayer.Visibility = Visibility.Collapsed;

        // Editable projects take the real scene-graph runtime path. Built-ins and
        // old single-image imports retain the lightweight compatibility renderer.
        if (LoadSceneGraph(scene))
        {
            BuiltInScene.Visibility = Visibility.Collapsed;
            return;
        }

        SceneGraphCanvas.Visibility = Visibility.Collapsed;
        BuiltInScene.Visibility = Visibility.Visible;

        if (!scene.IsBuiltIn)
        {
            var entry = scene.ResolveEntryPath();
            if (!string.IsNullOrWhiteSpace(entry) && File.Exists(entry))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(entry, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    StaticBackground.Background = new ImageBrush(image)
                    {
                        Stretch = scene.Fit switch
                        {
                            SceneFit.Contain => Stretch.Uniform,
                            SceneFit.Stretch => Stretch.Fill,
                            _ => Stretch.UniformToFill
                        },
                        AlignmentX = AlignmentX.Center,
                        AlignmentY = AlignmentY.Center
                    };
                }
                catch (Exception error)
                {
                    PlaybackError?.Invoke($"Image scene could not be loaded: {error.Message}");
                }
            }
        }

        var tag = scene.Tags.FirstOrDefault(tag => tag is "aurora" or "neon" or "orbit") ?? "aurora";
        GridLayer.Visibility = tag == "neon" ? Visibility.Visible : Visibility.Collapsed;
        var intensity = Math.Clamp(appearance.SceneIntensity, 0.2, 1.0);
        AuroraLayer.Opacity = scene.IsBuiltIn ? intensity : Math.Min(0.42, intensity * 0.45);

        switch (tag)
        {
            case "neon":
                GlowAColor.Color = Color.FromRgb(116, 78, 255);
                GlowBColor.Color = Color.FromRgb(29, 220, 198);
                CreateParticles(36, 1.7, 4.6);
                break;
            case "orbit":
                GlowAColor.Color = Color.FromRgb(70, 105, 185);
                GlowBColor.Color = Color.FromRgb(160, 121, 202);
                CreateParticles(58, 0.8, 2.8);
                break;
            default:
                GlowAColor.Color = Color.FromRgb(100, 119, 255);
                GlowBColor.Color = Color.FromRgb(84, 217, 196);
                CreateParticles(scene.IsBuiltIn ? 42 : 24, 1.1, 3.8);
                break;
        }

        if (appearance.SceneMotionEnabled)
        {
            StartGlowAnimation(GlowA, 24, 18, 11);
            StartGlowAnimation(GlowB, -20, -14, 13);
        }
    }

    private void LoadVideo(SceneManifest scene)
    {
        var entry = scene.ResolveEntryPath();
        if (string.IsNullOrWhiteSpace(entry) || !File.Exists(entry))
        {
            PlaybackError?.Invoke($"Video scene entry is missing: {scene.Entry}");
            return;
        }

        BuiltInScene.Visibility = Visibility.Collapsed;
        SceneGraphCanvas.Visibility = Visibility.Collapsed;
        WebPlayer.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;
        VideoPlayer.Stretch = scene.Fit switch
        {
            SceneFit.Contain => Stretch.Uniform,
            SceneFit.Stretch => Stretch.Fill,
            _ => Stretch.UniformToFill
        };
        VideoPlayer.Source = new Uri(entry, UriKind.Absolute);
        VideoPlayer.IsMuted = scene.Muted;
        VideoPlayer.Volume = scene.Muted ? 0 : 0.5;
        VideoPlayer.Play();
    }

    private async Task LoadWebAsync(SceneManifest scene, CancellationToken cancellationToken)
    {
        var entry = scene.ResolveEntryPath();
        if (string.IsNullOrWhiteSpace(entry) || !File.Exists(entry))
        {
            PlaybackError?.Invoke($"Web scene entry is missing: {scene.Entry}");
            return;
        }

        BuiltInScene.Visibility = Visibility.Collapsed;
        SceneGraphCanvas.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        WebPlayer.Visibility = Visibility.Visible;

        await WebPlayer.EnsureCoreWebView2Async();
        cancellationToken.ThrowIfCancellationRequested();
        WebPlayer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        WebPlayer.CoreWebView2.Settings.AreDevToolsEnabled = false;
        WebPlayer.CoreWebView2.Settings.IsZoomControlEnabled = false;
        WebPlayer.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        WebPlayer.NavigationCompleted -= WebPlayer_NavigationCompleted;
        WebPlayer.NavigationCompleted += WebPlayer_NavigationCompleted;
        WebPlayer.Source = new Uri(entry, UriKind.Absolute);
    }

    private async void WebPlayer_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || WebPlayer.CoreWebView2 is null) return;
        try
        {
            await WebPlayer.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                  window.turingDesk = window.turingDesk || {};
                  window.turingDesk.version = 1;
                  window.dispatchEvent(new CustomEvent('turingdesk-ready'));
                  if (window.wallpaperPropertyListener && typeof window.wallpaperPropertyListener.applyUserProperties === 'function') {
                    window.wallpaperPropertyListener.applyUserProperties({});
                  }
                })();
                """);
        }
        catch { }
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_scene?.Kind != SceneKind.Video || _stopped) return;
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        PlaybackError?.Invoke(e.ErrorException?.Message ?? "Video playback failed.");
    }

    private void CreateParticles(int count, double minSize, double maxSize)
    {
        ParticleCanvas.Children.Clear();
        var width = Math.Max(1920, SystemParameters.VirtualScreenWidth);
        var height = Math.Max(1080, SystemParameters.VirtualScreenHeight);

        for (var i = 0; i < count; i++)
        {
            var size = minSize + _random.NextDouble() * (maxSize - minSize);
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb((byte)_random.Next(90, 205), 222, 230, 255)),
                Opacity = 0.25 + _random.NextDouble() * 0.5
            };
            Canvas.SetLeft(dot, _random.NextDouble() * width);
            Canvas.SetTop(dot, _random.NextDouble() * height);
            ParticleCanvas.Children.Add(dot);

            var drift = new DoubleAnimation
            {
                From = Canvas.GetTop(dot),
                To = Canvas.GetTop(dot) - 90 - _random.NextDouble() * 170,
                Duration = TimeSpan.FromSeconds(10 + _random.NextDouble() * 18),
                RepeatBehavior = RepeatBehavior.Forever
            };
            dot.BeginAnimation(Canvas.TopProperty, drift);
        }
    }

    private static void StartGlowAnimation(FrameworkElement element, double x, double y, double seconds)
    {
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, x, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, y, TimeSpan.FromSeconds(seconds * 1.13))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void PauseBuiltInAnimations()
    {
        StopBuiltInAnimations();
    }

    private void ResumeBuiltInAnimations()
    {
        if (_scene is { Layers.Count: > 0 }) return;
        var appearance = new ShellSettingsStore().Load().Appearance;
        if (!appearance.SceneMotionEnabled) return;
        StartGlowAnimation(GlowA, 24, 18, 11);
        StartGlowAnimation(GlowB, -20, -14, 13);
    }

    private void StopBuiltInAnimations()
    {
        if (GlowA.RenderTransform is TranslateTransform a)
        {
            a.BeginAnimation(TranslateTransform.XProperty, null);
            a.BeginAnimation(TranslateTransform.YProperty, null);
        }
        if (GlowB.RenderTransform is TranslateTransform b)
        {
            b.BeginAnimation(TranslateTransform.XProperty, null);
            b.BeginAnimation(TranslateTransform.YProperty, null);
        }
        foreach (var dot in ParticleCanvas.Children.OfType<Ellipse>())
        {
            dot.BeginAnimation(Canvas.TopProperty, null);
        }
    }

    private async Task SetWebPlaybackStateAsync(bool paused)
    {
        if (WebPlayer.CoreWebView2 is null) return;
        try
        {
            await WebPlayer.CoreWebView2.ExecuteScriptAsync($"window.dispatchEvent(new CustomEvent('turingdesk-playback', {{detail: {{paused: {paused.ToString().ToLowerInvariant()}}}}}}));");
        }
        catch { }
    }

    private async Task SetWebVolumeAsync(double volume)
    {
        if (WebPlayer.CoreWebView2 is null) return;
        try
        {
            var jsVolume = volume.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WebPlayer.CoreWebView2.ExecuteScriptAsync($"document.querySelectorAll('audio,video').forEach(x => x.volume = {jsVolume});");
        }
        catch { }
    }
}
