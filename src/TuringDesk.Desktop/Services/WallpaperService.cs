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

    public static Brush? CreateCurrentWallpaperBrush()
    {
        var path = GetCurrentWallpaperPath();
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
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
