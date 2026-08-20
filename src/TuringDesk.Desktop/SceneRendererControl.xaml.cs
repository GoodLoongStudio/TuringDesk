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
    private bool _stopped = true;

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
        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        _scene = scene;
        _paused = false;
        _stopped = false;
        Root.Visibility = Visibility.Visible;
        ApplyStaticBackground(appearance);
        ConfigureGpuSurface(scene, appearance);

        try
        {
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

            cancellationToken.ThrowIfCancellationRequested();
            await ApplyUserPropertiesAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            // A superseded/cancelled scene must not leave a partially initialized
            // WebView, audio lease, script timer or GPU refresh loop behind.
            Stop();
            throw;
        }
    }

    /// <summary>
    /// Full playback suspension used by fullscreen policy. It stops every periodic
    /// producer owned by the renderer: GPU continuous refresh, WASAPI/FFT lease,
    /// SceneScript timer and desktop-input polling.
    /// </summary>
    public void Pause()
    {
        if (_scene is null || _paused || _stopped) return;
        _paused = true;

        ReleaseAudioBridge();
        PauseSceneScript();
        _desktopInputTimer.Stop();

        switch (_scene.Kind)
        {
            case SceneKind.Video:
                try { VideoPlayer.Pause(); } catch { }
                break;
            case SceneKind.Web:
                _ = SetWebPlaybackStateAsync(paused: true);
                WebPlayer.Visibility = Visibility.Collapsed;
                break;
            default:
                GpuSurface.SetPaused(true);
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
                try { VideoPlayer.Play(); } catch { }
                break;
            case SceneKind.Web:
                WebPlayer.Visibility = Visibility.Visible;
                _ = SetWebPlaybackStateAsync(paused: false);
                break;
            default:
                GpuSurface.SetPaused(false);
                ResumeBuiltInAnimations();
                ResumeSceneGraphAnimations();
                break;
        }

        ResumeSceneScript();
        UpdateAudioLeaseForCurrentScene();
        if (IsLoaded) _desktopInputTimer.Start();
    }

    /// <summary>
    /// Deterministic teardown for scene switching and window close. This method is
    /// intentionally idempotent because LoadAsync calls it before every scene.
    /// </summary>
    public void Stop()
    {
        _paused = false;
        _stopped = true;

        _desktopInputTimer.Stop();
        StopSceneScript();
        ShutdownAudioBridge();
        StopBuiltInAnimations();
        StopSceneGraph();

        GpuSurface.StopRendering();
        GpuSurface.Visibility = Visibility.Collapsed;

        try { VideoPlayer.Stop(); } catch { }
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;

        WebPlayer.NavigationCompleted -= WebPlayer_NavigationCompleted;
        if (WebPlayer.CoreWebView2 is not null)
        {
            try { WebPlayer.CoreWebView2.Navigate("about:blank"); } catch { }
        }
        WebPlayer.Visibility = Visibility.Collapsed;

        BuiltInScene.Visibility = Visibility.Collapsed;
        ParticleCanvas.Children.Clear();
        _lastInput = null;
        _scene = null;

        // Release large decoded image brushes immediately instead of retaining the
        // previous scene until a later load overwrites the background.
        StaticBackground.Background = new SolidColorBrush(Color.FromRgb(8, 10, 16));
        Root.Visibility = Visibility.Visible;
    }

    public void SetVolume(double volume, bool muted)
    {
        var normalized = Math.Clamp(volume, 0, 1);
        VideoPlayer.Volume = muted ? 0 : normalized;
        VideoPlayer.IsMuted = muted;
        if (_scene?.Kind == SceneKind.Web)
            _ = SetWebVolumeAsync(muted ? 0 : normalized);
    }

    private void ApplyStaticBackground(ShellAppearanceSettings appearance)
    {
        StaticBackground.Background = WallpaperService.CreateWallpaperBrush(appearance)
            ?? new SolidColorBrush(Color.FromRgb(8, 10, 16));
    }

    private void ConfigureGpuSurface(SceneManifest scene, ShellAppearanceSettings appearance)
    {
        if (scene.Kind != SceneKind.Scene)
        {
            GpuSurface.StopRendering();
            GpuSurface.Visibility = Visibility.Collapsed;
            return;
        }

        var preset = scene.Tags.FirstOrDefault(tag => tag is "aurora" or "neon" or "orbit") ?? "aurora";
        GpuSurface.Configure(preset, appearance.SceneIntensity, appearance.SceneMotionEnabled);
        GpuSurface.Visibility = Visibility.Visible;
    }

    private void LoadScene(SceneManifest scene, ShellAppearanceSettings appearance)
    {
        VideoPlayer.Visibility = Visibility.Collapsed;
        WebPlayer.Visibility = Visibility.Collapsed;

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
        AuroraLayer.Opacity = scene.IsBuiltIn
            ? Math.Clamp(0.76 + intensity * 0.24, 0.80, 1.0)
            : Math.Min(0.48, intensity * 0.50);

        switch (tag)
        {
            case "neon":
                GlowAColor.Color = Color.FromRgb(255, 54, 209);
                GlowBColor.Color = Color.FromRgb(35, 245, 219);
                GlowCColor.Color = Color.FromRgb(75, 102, 255);
                GlowA.Opacity = 0.78;
                GlowB.Opacity = 0.72;
                GlowC.Opacity = 0.58;
                GridLayer.Opacity = 0.34;
                CreateParticles(
                    88,
                    1.8,
                    5.8,
                    Color.FromRgb(255, 102, 232),
                    Color.FromRgb(75, 255, 230),
                    Color.FromRgb(126, 143, 255));
                break;
            case "orbit":
                GlowAColor.Color = Color.FromRgb(61, 116, 255);
                GlowBColor.Color = Color.FromRgb(172, 93, 255);
                GlowCColor.Color = Color.FromRgb(255, 184, 72);
                GlowA.Opacity = 0.68;
                GlowB.Opacity = 0.62;
                GlowC.Opacity = 0.46;
                GridLayer.Opacity = 0.20;
                CreateParticles(
                    74,
                    1.0,
                    4.2,
                    Color.FromRgb(125, 175, 255),
                    Color.FromRgb(211, 157, 255),
                    Color.FromRgb(255, 213, 130));
                break;
            default:
                GlowAColor.Color = Color.FromRgb(126, 82, 255);
                GlowBColor.Color = Color.FromRgb(44, 236, 210);
                GlowCColor.Color = Color.FromRgb(255, 92, 184);
                GlowA.Opacity = 0.72;
                GlowB.Opacity = 0.67;
                GlowC.Opacity = 0.52;
                GridLayer.Opacity = 0.24;
                CreateParticles(
                    scene.IsBuiltIn ? 68 : 28,
                    1.2,
                    4.8,
                    Color.FromRgb(174, 135, 255),
                    Color.FromRgb(91, 255, 225),
                    Color.FromRgb(255, 137, 205));
                break;
        }

        if (appearance.SceneMotionEnabled)
            StartBuiltInGlowAnimations(tag);
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
        GpuSurface.StopRendering();
        GpuSurface.Visibility = Visibility.Collapsed;
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
        GpuSurface.StopRendering();
        GpuSurface.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        WebPlayer.Visibility = Visibility.Visible;

        cancellationToken.ThrowIfCancellationRequested();
        await WebPlayer.EnsureCoreWebView2Async();
        cancellationToken.ThrowIfCancellationRequested();
        await InstallWebAudioCompatibilityBridgeAsync();
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
            await WebPlayer.CoreWebView2.ExecuteScriptAsync(
                "window.turingDesk=window.turingDesk||{};window.turingDesk.version=1;window.dispatchEvent(new CustomEvent('turingdesk-ready'));" );
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

    private void CreateParticles(int count, double minSize, double maxSize, params Color[] palette)
    {
        ParticleCanvas.Children.Clear();
        var width = Math.Max(1920, ActualWidth > 1 ? ActualWidth : SystemParameters.PrimaryScreenWidth);
        var height = Math.Max(1080, ActualHeight > 1 ? ActualHeight : SystemParameters.PrimaryScreenHeight);
        var colors = palette.Length == 0
            ? [Color.FromRgb(222, 230, 255)]
            : palette;

        for (var i = 0; i < count; i++)
        {
            var size = minSize + _random.NextDouble() * (maxSize - minSize);
            var baseColor = colors[_random.Next(colors.Length)];
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(
                    (byte)_random.Next(105, 225),
                    baseColor.R,
                    baseColor.G,
                    baseColor.B)),
                Opacity = 0.28 + _random.NextDouble() * 0.58
            };
            var initialLeft = _random.NextDouble() * width;
            var initialTop = _random.NextDouble() * height;
            Canvas.SetLeft(dot, initialLeft);
            Canvas.SetTop(dot, initialTop);
            ParticleCanvas.Children.Add(dot);

            var duration = 9 + _random.NextDouble() * 18;
            dot.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
            {
                From = initialTop,
                To = initialTop - 120 - _random.NextDouble() * 260,
                Duration = TimeSpan.FromSeconds(duration),
                RepeatBehavior = RepeatBehavior.Forever
            });
            dot.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
            {
                From = initialLeft,
                To = initialLeft + (_random.NextDouble() > 0.5 ? 1 : -1) * (35 + _random.NextDouble() * 160),
                Duration = TimeSpan.FromSeconds(duration * (0.72 + _random.NextDouble() * 0.55)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }

    private void StartBuiltInGlowAnimations(string tag)
    {
        switch (tag)
        {
            case "neon":
                StartGlowAnimation(GlowA, 150, 92, 8.2);
                StartGlowAnimation(GlowB, -138, -74, 9.4);
                StartGlowAnimation(GlowC, 104, -118, 7.6);
                break;
            case "orbit":
                StartGlowAnimation(GlowA, 82, 54, 16.5);
                StartGlowAnimation(GlowB, -76, -92, 18.0);
                StartGlowAnimation(GlowC, 118, 48, 13.6);
                break;
            default:
                StartGlowAnimation(GlowA, 112, 72, 12.0);
                StartGlowAnimation(GlowB, -96, -64, 14.5);
                StartGlowAnimation(GlowC, 62, -104, 10.2);
                break;
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

    private void PauseBuiltInAnimations() => StopBuiltInAnimations();

    private void ResumeBuiltInAnimations()
    {
        if (_scene is { Layers.Count: > 0 }) return;
        var appearance = new ShellSettingsStore().Load().Appearance;
        if (!appearance.SceneMotionEnabled || _scene is null) return;
        var tag = _scene.Tags.FirstOrDefault(item => item is "aurora" or "neon" or "orbit") ?? "aurora";
        StartBuiltInGlowAnimations(tag);
    }

    private void StopBuiltInAnimations()
    {
        foreach (var glow in new[] { GlowA, GlowB, GlowC })
        {
            if (glow.RenderTransform is not TranslateTransform transform) continue;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
        }

        foreach (var dot in ParticleCanvas.Children.OfType<Ellipse>())
        {
            dot.BeginAnimation(Canvas.TopProperty, null);
            dot.BeginAnimation(Canvas.LeftProperty, null);
        }
    }

    private async Task SetWebPlaybackStateAsync(bool paused)
    {
        if (WebPlayer.CoreWebView2 is null) return;
        try
        {
            var pausedText = paused ? "true" : "false";
            var script = "window.dispatchEvent(new CustomEvent('turingdesk-playback',{detail:{paused:" + pausedText + "}}));";
            await WebPlayer.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }

    private async Task SetWebVolumeAsync(double volume)
    {
        if (WebPlayer.CoreWebView2 is null) return;
        try
        {
            var jsVolume = volume.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WebPlayer.CoreWebView2.ExecuteScriptAsync(
                "document.querySelectorAll('audio,video').forEach(x=>x.volume=" + jsVolume + ");");
        }
        catch { }
    }
}
