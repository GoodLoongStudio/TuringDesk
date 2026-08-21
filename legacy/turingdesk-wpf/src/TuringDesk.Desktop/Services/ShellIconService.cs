using System.Collections.Concurrent;
using System.IO;
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
    AudioFiles = 71,
    ImageFiles = 72,
    VideoFiles = 73,
    Shield = 77,
    Warning = 78,
    Info = 79,
    Error = 80,
    Software = 82,
    Rename = 83,
    Delete = 84,
    DesktopPc = 94,
    Users = 96,
    NetworkConnect = 103,
    Internet = 104,
    ZipFile = 105,
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

    private const uint SiigbfBiggerSizeOk = 0x00000001;
    private const uint SiigbfIconOnly = 0x00000004;

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

    public static ImageSource? GetPackagedAppIcon(string appUserModelId, bool large = false)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return null;
        var key = $"AUMID|{(large ? "L" : "S")}|{appUserModelId}";
        return Cache.GetOrAdd(key, _ => LoadPackagedAppIcon(appUserModelId, large));
    }

    public static ImageSource? GetApplicationIcon(string target, bool large = false)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        if (target.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
        {
            return GetPackagedAppIcon(target["aumid:".Length..], large)
                ?? GetStockIcon(ShellStockIconId.Application, large);
        }

        if (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            return GetStockIcon(ShellStockIconId.Settings, large)
                ?? GetSystemExecutableIcon("SystemSettings.exe", large);
        }

        if (File.Exists(target) || Directory.Exists(target))
            return GetIcon(target, large) ?? GetStockIcon(ShellStockIconId.Application, large);

        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return GetSystemExecutableIcon(target, large) ?? GetStockIcon(ShellStockIconId.Application, large);

        return GetStockIcon(ShellStockIconId.Application, large);
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

    private static ImageSource? LoadPackagedAppIcon(string appUserModelId, bool large)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var parsingName = $"shell:AppsFolder\\{appUserModelId}";
            var result = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory);
            if (result < 0 || factory is null) return null;

            var requestedSize = large ? 48 : 32;
            var size = new NativeSize { Cx = requestedSize, Cy = requestedSize };
            result = factory.GetImage(size, SiigbfIconOnly | SiigbfBiggerSizeOk, out var bitmap);
            if (result < 0 || bitmap == IntPtr.Zero) return null;

            return ConvertAndDeleteBitmap(bitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory is not null && Marshal.IsComObject(factory))
            {
                try { Marshal.FinalReleaseComObject(factory); } catch { }
            }
        }
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

    private static ImageSource? ConvertAndDeleteBitmap(IntPtr bitmapHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
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
            _ = DeleteObject(bitmapHandle);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr phbm);
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
