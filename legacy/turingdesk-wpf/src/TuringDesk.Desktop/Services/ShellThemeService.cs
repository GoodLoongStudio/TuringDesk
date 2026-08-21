using System.Windows;
using System.Windows.Media;

namespace TuringDesk.Desktop.Services;

public static class ShellThemeService
{
    public static void Apply(ShellAppearanceSettings appearance)
    {
        if (Application.Current is null) return;
        if (!TryParseColor(appearance.AccentHex, out var accent)) return;

        Application.Current.Resources["AccentBrush"] = FrozenBrush(accent);
        Application.Current.Resources["AccentStrongBrush"] = FrozenBrush(Darken(accent, 0.12));
        Application.Current.Resources["AccentSoftBrush"] = FrozenBrush(Color.FromArgb(72, accent.R, accent.G, accent.B));
    }

    public static SolidColorBrush AccentBrush(ShellAppearanceSettings appearance)
    {
        return TryParseColor(appearance.AccentHex, out var accent)
            ? FrozenBrush(accent)
            : FrozenBrush(Color.FromRgb(135, 150, 255));
    }

    private static bool TryParseColor(string value, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    private static Color Darken(Color color, double amount)
    {
        var factor = Math.Clamp(1.0 - amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}