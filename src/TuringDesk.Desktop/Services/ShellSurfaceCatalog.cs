using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace TuringDesk.Desktop.Services;

public sealed record DesktopSurfaceItem(
    string Name,
    string Path,
    string Glyph,
    string Kind,
    string Subtitle,
    bool IsDirectory,
    ImageSource? Icon,
    string IconKind = "File");

public sealed record StartAppItem(
    string Name,
    string Target,
    string Glyph,
    string Category,
    ImageSource? Icon,
    string IconKind = "App");

public static class ShellSurfaceCatalog
{
    private static readonly string[] StartMenuExtensions = [".lnk", ".url", ".appref-ms"];

    public static IReadOnlyList<DesktopSurfaceItem> LoadDesktopItems()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        var items = new Dictionary<string, DesktopSurfaceItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    if (ShouldHide(directory)) continue;
                    var name = Path.GetFileName(directory);
                    items.TryAdd(directory, new DesktopSurfaceItem(
                        name,
                        directory,
                        "▣",
                        "folder",
                        "文件夹",
                        true,
                        ShellIconService.GetIcon(directory, large: true)
                            ?? ShellIconService.GetStockIcon(ShellStockIconId.Folder, large: true),
                        "Folder"));
                }

                foreach (var file in Directory.EnumerateFiles(root))
                {
                    if (ShouldHide(file)) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    items.TryAdd(file, new DesktopSurfaceItem(
                        name,
                        file,
                        GlyphForExtension(extension),
                        extension.TrimStart('.'),
                        SubtitleForExtension(extension),
                        false,
                        ShellIconService.GetIcon(file, large: true) ?? NativeFallbackForExtension(extension, large: true),
                        IconKindForExtension(extension)));
                }
            }
            catch
            {
                // Desktop contents can change while we enumerate them. The next refresh will retry.
            }
        }

        return items.Values
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(240)
            .ToArray();
    }

    /// <summary>
    /// Enumerate launchable application shortcuts from the Windows Start Menu,
    /// desktop and taskbar-pinned shortcut locations. Search callers pass
    /// includeIcons:false so the always-resident RAM index does not decode hundreds
    /// of shell bitmaps. This intentionally avoids a full executable scan.
    /// </summary>
    public static IReadOnlyList<StartAppItem> LoadStartApps(bool includeIcons = true)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(userProfile, "Downloads");

        ImageSource? SettingsIcon() => includeIcons
            ? ShellIconService.GetStockIcon(ShellStockIconId.Settings, large: false)
              ?? ShellIconService.GetSystemExecutableIcon("SystemSettings.exe", large: false)
            : null;
        ImageSource? FolderIcon(string path) => includeIcons
            ? ShellIconService.GetIcon(path, large: false)
              ?? ShellIconService.GetStockIcon(ShellStockIconId.Folder, large: false)
            : null;
        ImageSource? AppIcon(string path) => includeIcons
            ? ShellIconService.GetIcon(path, large: false)
              ?? ShellIconService.GetStockIcon(ShellStockIconId.Application, large: false)
            : null;

        var items = new Dictionary<string, StartAppItem>(StringComparer.CurrentCultureIgnoreCase)
        {
            ["设置"] = new(
                "设置",
                "ms-settings:",
                "⚙",
                "系统",
                SettingsIcon(),
                "Settings"),
            ["文件"] = new(
                "文件",
                userProfile,
                "▣",
                "系统",
                FolderIcon(userProfile),
                "Folder"),
            ["文档"] = new(
                "文档",
                documents,
                "≡",
                "文件夹",
                FolderIcon(documents),
                "Folder"),
            ["下载"] = new(
                "下载",
                downloads,
                "↓",
                "文件夹",
                FolderIcon(downloads),
                "Folder")
        };

        var launchRoots = new[]
        {
            new LaunchRoot(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "开始菜单", recursive: true),
            new LaunchRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "公共开始菜单", recursive: true),
            new LaunchRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面快捷方式", recursive: false),
            new LaunchRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面快捷方式", recursive: false),
            new LaunchRoot(
                Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
                "任务栏固定",
                recursive: true)
        };

        foreach (var launchRoot in launchRoots
                     .Where(root => Directory.Exists(root.Path))
                     .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var option = launchRoot.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(launchRoot.Path, "*.*", option))
                {
                    var extension = Path.GetExtension(file);
                    if (!StartMenuExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
                    if (ShouldHide(file)) continue;

                    var name = Path.GetFileNameWithoutExtension(file).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var category = launchRoot.Label;
                    if (launchRoot.Recursive &&
                        launchRoot.Label.Contains("开始菜单", StringComparison.Ordinal) &&
                        Path.GetDirectoryName(file) is { } directory)
                    {
                        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(launchRoot.Path, file)) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
                            category = relativeDirectory.Split(Path.DirectorySeparatorChar)[0];
                    }

                    items.TryAdd(name, new StartAppItem(
                        name,
                        file,
                        "◆",
                        category,
                        AppIcon(file),
                        "App"));
                }
            }
            catch
            {
                // Shortcut locations can change while the index is being built.
            }
        }

        return items.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(800)
            .ToArray();
    }

    public static bool OpenTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenContainingFolder(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            if (Directory.Exists(target))
            {
                var parent = Directory.GetParent(target)?.FullName ?? target;
                return OpenTarget(parent);
            }

            if (!File.Exists(target)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ShowProperties(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target))) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Verb = "properties",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ImageSource? NativeFallbackForExtension(string extension, bool large) => extension switch
    {
        ".lnk" or ".url" or ".appref-ms" => ShellIconService.GetStockIcon(ShellStockIconId.Link, large),
        ".exe" or ".msi" => ShellIconService.GetStockIcon(ShellStockIconId.Application, large),
        ".txt" or ".md" or ".doc" or ".docx" or ".pdf" => ShellIconService.GetStockIcon(ShellStockIconId.DocumentAssociation, large),
        _ => ShellIconService.GetStockIcon(ShellStockIconId.DocumentNoAssociation, large)
    };

    private static bool ShouldHide(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return true;
        }
    }

    private static string IconKindForExtension(string extension) => extension switch
    {
        ".txt" or ".md" or ".doc" or ".docx" or ".pdf" => "TextFile",
        ".lnk" or ".url" or ".appref-ms" or ".exe" or ".msi" => "App",
        _ => "File"
    };

    private static string GlyphForExtension(string extension) => extension switch
    {
        ".lnk" or ".url" => "↗",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "▧",
        ".txt" or ".md" or ".doc" or ".docx" or ".pdf" => "≡",
        ".zip" or ".7z" or ".rar" => "▤",
        ".exe" or ".msi" => "◇",
        _ => "□"
    };

    private static string SubtitleForExtension(string extension) => extension switch
    {
        ".lnk" => "快捷方式",
        ".url" => "网址",
        "" => "文件",
        _ => $"{extension.TrimStart('.').ToUpperInvariant()} 文件"
    };

    private sealed record LaunchRoot(string Path, string Label, bool Recursive);
}
