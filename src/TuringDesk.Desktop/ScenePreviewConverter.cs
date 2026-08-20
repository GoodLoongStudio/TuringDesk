using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

/// <summary>
/// Generates a distinct gradient brush per scene so library cards are visually
/// different at a glance. Each built-in scene preset gets its own palette;
/// user scenes get a neutral gradient. This is not a full dynamic preview but
/// gives each scene a recognizable identity in the card grid.
/// </summary>
public sealed class ScenePreviewConverter : IValueConverter
{
    private static readonly LinearGradientBrush Aurora = Create("#0B1E3F", "#1B4F72", "#2E86C1");
    private static readonly LinearGradientBrush Neon = Create("#0D0D0D", "#1A0033", "#003B46");
    private static readonly LinearGradientBrush Orbit = Create("#09090F", "#1A1A2E", "#3D1E6D");
    private static readonly LinearGradientBrush Default = Create("#1A1A2E", "#16213E", "#0F3460");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SceneManifest scene) return Default;

        if (scene.Id.Contains("aurora", StringComparison.OrdinalIgnoreCase))
            return Aurora;
        if (scene.Id.Contains("neon", StringComparison.OrdinalIgnoreCase))
            return Neon;
        if (scene.Id.Contains("orbit", StringComparison.OrdinalIgnoreCase))
            return Orbit;

        return Default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static LinearGradientBrush Create(string c1, string c2, string c3)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop((Color)ColorConverter.ConvertFromString(c1), 0),
                new GradientStop((Color)ColorConverter.ConvertFromString(c2), 0.5),
                new GradientStop((Color)ColorConverter.ConvertFromString(c3), 1),
            }
        };
        brush.Freeze();
        return brush;
    }
}
