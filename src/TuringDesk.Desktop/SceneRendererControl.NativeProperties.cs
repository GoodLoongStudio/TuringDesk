using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private bool _nativePropertyHooked;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (_nativePropertyHooked) return;
        _nativePropertyHooked = true;
        SceneInstanceSettingsStore.SceneSettingsChanged += NativeSceneSettingsChanged;
        Unloaded += (_, _) => SceneInstanceSettingsStore.SceneSettingsChanged -= NativeSceneSettingsChanged;
    }

    private void NativeSceneSettingsChanged(string sceneId)
    {
        if (_scene is null || !string.Equals(_scene.Id, sceneId, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(new Action(ApplyNativeSceneProperties));
    }

    private void ApplyNativeSceneProperties()
    {
        if (_scene is null || _scene.Kind != SceneKind.Scene) return;
        var settings = _instanceSettingsStore.Load(_scene);
        var intensity = ReadNumber(settings, "intensity", 0.85);
        var motion = ReadBool(settings, "motion", true);
        var accent = ReadText(settings, "accent", "#8796FF");
        var preset = _scene.Tags.FirstOrDefault(tag => tag is "aurora" or "neon" or "orbit") ?? "aurora";

        GpuSurface.Configure(preset, intensity, motion);
        if (!motion) GpuSurface.SetPaused(true);

        if (BuiltInScene.Visibility == Visibility.Visible)
        {
            AuroraLayer.Opacity = Math.Clamp(intensity, 0.2, 1.0);
            if (TryParseColor(accent, out var color))
            {
                GlowAColor.Color = color;
                GlowBColor.Color = Color.FromArgb(
                    color.A,
                    (byte)Math.Clamp((int)(color.R * 0.72 + 35), 0, 255),
                    (byte)Math.Clamp((int)(color.G * 0.86 + 20), 0, 255),
                    (byte)Math.Clamp((int)(color.B * 0.78 + 32), 0, 255));
            }

            if (motion)
            {
                StartGlowAnimation(GlowA, 24, 18, 11);
                StartGlowAnimation(GlowB, -20, -14, 13);
            }
            else
            {
                StopBuiltInAnimations();
            }
        }
    }

    private static double ReadNumber(SceneInstanceSettings settings, string key, double fallback)
    {
        if (!settings.Properties.TryGetValue(key, out var value) || value is null) return fallback;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonNumber)) return jsonNumber;
        return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool ReadBool(SceneInstanceSettings settings, string key, bool fallback)
    {
        if (!settings.Properties.TryGetValue(key, out var value) || value is null) return fallback;
        if (value is bool boolean) return boolean;
        if (value is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        return bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    private static string ReadText(SceneInstanceSettings settings, string key, string fallback)
    {
        if (!settings.Properties.TryGetValue(key, out var value) || value is null) return fallback;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.String) return element.GetString() ?? fallback;
        return value.ToString() ?? fallback;
    }

    private static bool TryParseColor(string value, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }
}
