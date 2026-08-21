using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TuringDesk.Desktop.Services;

public static class WallpaperService
{
    private const uint SpiGetDeskWallpaper = 0x0073;

    public static string? GetCurrentWallpaperPath()
    {
        var buffer = new StringBuilder(1024);
        return SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Capacity, buffer, 0) && File.Exists(buffer.ToString())
            ? buffer.ToString()
            : null;
    }

    public static string? ResolveWallpaperPath(ShellAppearanceSettings? appearance)
    {
        if (appearance?.WallpaperMode == "custom" && !string.IsNullOrWhiteSpace(appearance.WallpaperPath) && File.Exists(appearance.WallpaperPath))
        {
            return appearance.WallpaperPath;
        }

        return appearance?.WallpaperMode == "solid" ? null : GetCurrentWallpaperPath();
    }

    public static Brush? CreateCurrentWallpaperBrush() => CreateWallpaperBrush(GetCurrentWallpaperPath(), "cover");

    public static Brush? CreateWallpaperBrush(ShellAppearanceSettings? appearance)
    {
        if (appearance?.WallpaperMode == "solid")
        {
            return new SolidColorBrush(Color.FromRgb(12, 15, 22));
        }

        return CreateWallpaperBrush(ResolveWallpaperPath(appearance), appearance?.WallpaperFit ?? "cover");
    }

    public static Brush? CreateWallpaperBrush(string? path, string fit)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var stretch = fit switch
            {
                "contain" => Stretch.Uniform,
                "stretch" => Stretch.Fill,
                _ => Stretch.UniformToFill
            };

            var brush = new ImageBrush(bitmap)
            {
                Stretch = stretch,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);
}