using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TuringDesk.Desktop.Services;

public enum ShellStockIconId
{
    DocumentNoAssociation = 0,
    DocumentAssociation = 1,
    Application = 2,
    Folder = 3,
    FolderOpen = 4,
    DriveFixed = 8,
    World = 13,
    Server = 15,
    Printer = 16,
    Network = 17,
    Find = 22,
    Help = 23,
    Share = 28,
    Link = 29,
    Recycler = 31,
    RecyclerFull = 32,
    Lock = 47,
    Settings = 106
}

public static class ShellIconService
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiLinkOverlay = 0x00008000;

    private const uint ShgsiIcon = 0x00000100;
    private const uint ShgsiLargeIcon = 0x00000000;
    private const uint ShgsiSmallIcon = 0x00000001;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path, bool large = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = $"PATH|{(large ? "L" : "S")}|{path}";
        return Cache.GetOrAdd(key, _ => LoadIcon(path, large));
    }

    public static ImageSource? GetStockIcon(ShellStockIconId iconId, bool large = true)
    {
        var key = $"STOCK|{(large ? "L" : "S")}|{(int)iconId}";
        return Cache.GetOrAdd(key, _ => LoadStockIcon(iconId, large));
    }

    public static ImageSource? GetSystemExecutableIcon(string executableName, bool large = true)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return null;
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory)) return null;

        var path = Path.Combine(systemDirectory, executableName);
        return File.Exists(path) ? GetIcon(path, large) : null;
    }

    private static ImageSource? LoadIcon(string path, bool large)
    {
        var flags = ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) flags |= ShgfiLinkOverlay;

        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        return ConvertAndDestroy(info.hIcon);
    }

    private static ImageSource? LoadStockIcon(ShellStockIconId iconId, bool large)
    {
        var info = new ShStockIconInfo
        {
            cbSize = (uint)Marshal.SizeOf<ShStockIconInfo>(),
            szPath = string.Empty
        };

        var flags = ShgsiIcon | (large ? ShgsiLargeIcon : ShgsiSmallIcon);
        var result = SHGetStockIconInfo(iconId, flags, ref info);
        if (result != 0 || info.hIcon == IntPtr.Zero) return null;

        return ConvertAndDestroy(info.hIcon);
    }

    private static ImageSource? ConvertAndDestroy(IntPtr iconHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = DestroyIcon(iconHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShStockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(
        ShellStockIconId siid,
        uint uFlags,
        ref ShStockIconInfo psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
