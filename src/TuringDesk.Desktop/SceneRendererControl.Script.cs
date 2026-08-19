using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private readonly DispatcherTimer _sceneScriptTimer = new();
    private readonly Stopwatch _sceneScriptClock = new();
    private SceneScriptRuntime? _sceneScriptRuntime;
    private string? _sceneScriptSceneId;
    private double _lastScriptSeconds;
    private SceneInstanceSettings? _scriptInstanceSettings;
    private AudioSpectrumFrame? _scriptAudioFrame;
    private bool _sceneScriptTimerHooked;

    private void EnsureSceneScriptForCurrentScene(SceneInstanceSettings settings)
    {
        _scriptInstanceSettings = settings;
        if (_scene is null || string.IsNullOrWhiteSpace(_scene.Script))
        {
            StopSceneScript();
            return;
        }

        if (_sceneScriptRuntime is not null &&
            string.Equals(_sceneScriptSceneId, _scene.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (!_paused && !_stopped) ResumeSceneScript();
            return;
        }

        StopSceneScript();
        try
        {
            var root = Path.GetFullPath(_scene.PackageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var scriptPath = Path.GetFullPath(Path.Combine(_scene.PackageRoot, _scene.Script));
            if (!scriptPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(scriptPath))
                throw new InvalidDataException("SceneScript file is missing or outside the scene package.");
            if (new FileInfo(scriptPath).Length > 256 * 1024)
                throw new InvalidDataException("SceneScript is larger than the 256 KB safety limit.");

            var source = File.ReadAllText(scriptPath);
            var runtime = new SceneScriptRuntime(
                source,
                SetScriptLayerNumber,
                GetScriptLayerNumber,
                GetScriptNumberProperty,
                GetScriptTextProperty,
                GetScriptBoolProperty);
            if (runtime.IsFaulted)
            {
                runtime.Dispose();
                throw new InvalidDataException(runtime.LastError ?? "SceneScript could not start.");
            }

            _sceneScriptRuntime = runtime;
            _sceneScriptSceneId = _scene.Id;
            var fps = Math.Clamp(settings.FpsLimit > 0 ? settings.FpsLimit : _scene.PreferredFps, 10, 120);
            _sceneScriptTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            if (!_sceneScriptTimerHooked)
            {
                _sceneScriptTimer.Tick += SceneScriptTimer_Tick;
                _sceneScriptTimerHooked = true;
            }

            ResumeSceneScript();
        }
        catch (Exception error)
        {
            StopSceneScript();
            PlaybackError?.Invoke("SceneScript: " + error.Message);
        }
    }

    private void PauseSceneScript()
    {
        _sceneScriptTimer.Stop();
        _sceneScriptClock.Stop();
    }

    private void ResumeSceneScript()
    {
        if (_sceneScriptRuntime is null || _paused || _stopped) return;
        _lastScriptSeconds = 0;
        _sceneScriptClock.Restart();
        _sceneScriptTimer.Start();
    }

    private void StopSceneScript()
    {
        _sceneScriptTimer.Stop();
        _sceneScriptClock.Stop();
        _sceneScriptRuntime?.Dispose();
        _sceneScriptRuntime = null;
        _sceneScriptSceneId = null;
        _scriptInstanceSettings = null;
        _scriptAudioFrame = null;
        _lastScriptSeconds = 0;
    }

    private void SceneScriptTimer_Tick(object? sender, EventArgs e)
    {
        if (_sceneScriptRuntime is null || _scene is null || _paused || _stopped) return;
        var now = _sceneScriptClock.Elapsed.TotalSeconds;
        var delta = Math.Clamp(now - _lastScriptSeconds, 0, 0.1);
        _lastScriptSeconds = now;
        var pointer = DesktopInputBridge.Capture();
        var audio = _scriptAudioFrame;
        var frame = new SceneScriptFrame(
            now,
            delta,
            pointer?.NormalizedX ?? 0.5,
            pointer?.NormalizedY ?? 0.5,
            pointer?.LeftDown ?? false,
            audio?.Bass ?? 0,
            audio?.Mid ?? 0,
            audio?.Treble ?? 0,
            audio?.Rms ?? 0);

        if (_sceneScriptRuntime.Tick(frame)) return;
        var error = _sceneScriptRuntime.LastError ?? "SceneScript stopped after an execution error.";
        StopSceneScript();
        PlaybackError?.Invoke("SceneScript: " + error);
    }

    private void SetScriptLayerNumber(string layerId, string property, double value)
    {
        if (!_sceneGraphElements.TryGetValue(layerId, out var element) || !_sceneGraphLayers.TryGetValue(layerId, out var layer)) return;
        property = property.Trim().ToLowerInvariant();
        switch (property)
        {
            case "opacity":
                element.Opacity = Math.Clamp(value, 0, 1);
                break;
            case "x":
                layer.X = value;
                Canvas.SetLeft(element, ResolveScriptCoordinate(value, ActualWidth > 1 ? ActualWidth : SystemParameters.VirtualScreenWidth) - element.Width / 2);
                break;
            case "y":
                layer.Y = value;
                Canvas.SetTop(element, ResolveScriptCoordinate(value, ActualHeight > 1 ? ActualHeight : SystemParameters.VirtualScreenHeight) - element.Height / 2);
                break;
            case "rotation":
                if (FindTransform<RotateTransform>(element) is { } rotate) rotate.Angle = value;
                break;
            case "scale":
                if (FindTransform<ScaleTransform>(element) is { } scale)
                {
                    var normalized = Math.Clamp(value, 0.01, 100);
                    scale.ScaleX = normalized;
                    scale.ScaleY = normalized;
                }
                break;
            case "width":
                element.Width = Math.Max(1, ResolveScriptSize(value, ActualWidth > 1 ? ActualWidth : SystemParameters.VirtualScreenWidth));
                break;
            case "height":
                element.Height = Math.Max(1, ResolveScriptSize(value, ActualHeight > 1 ? ActualHeight : SystemParameters.VirtualScreenHeight));
                break;
        }
    }

    private double GetScriptLayerNumber(string layerId, string property)
    {
        if (!_sceneGraphElements.TryGetValue(layerId, out var element) || !_sceneGraphLayers.TryGetValue(layerId, out var layer)) return 0;
        return property.Trim().ToLowerInvariant() switch
        {
            "opacity" => element.Opacity,
            "x" => layer.X,
            "y" => layer.Y,
            "rotation" => FindTransform<RotateTransform>(element)?.Angle ?? layer.Rotation,
            "scale" => FindTransform<ScaleTransform>(element)?.ScaleX ?? layer.Scale,
            "width" => element.Width,
            "height" => element.Height,
            _ => 0
        };
    }

    private double GetScriptNumberProperty(string key)
    {
        if (_scriptInstanceSettings?.Properties.TryGetValue(key, out var value) != true) return 0;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonNumber)) return jsonNumber;
        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private string GetScriptTextProperty(string key)
    {
        if (_scriptInstanceSettings?.Properties.TryGetValue(key, out var value) != true) return string.Empty;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.String) return element.GetString() ?? string.Empty;
        return value?.ToString() ?? string.Empty;
    }

    private bool GetScriptBoolProperty(string key)
    {
        if (_scriptInstanceSettings?.Properties.TryGetValue(key, out var value) != true) return false;
        if (value is bool boolean) return boolean;
        if (value is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static T? FindTransform<T>(FrameworkElement element) where T : Transform
    {
        if (element.RenderTransform is T direct) return direct;
        if (element.RenderTransform is TransformGroup group) return group.Children.OfType<T>().FirstOrDefault();
        return null;
    }

    private static double ResolveScriptCoordinate(double value, double extent) => Math.Abs(value) <= 1.5 ? extent * value : value;
    private static double ResolveScriptSize(double value, double extent) => Math.Abs(value) <= 1.5 ? extent * value : value;
}
