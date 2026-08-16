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

    public static IReadOnlyList<StartAppItem> LoadStartApps()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(userProfile, "Downloads");

        var items = new Dictionary<string, StartAppItem>(StringComparer.CurrentCultureIgnoreCase)
        {
            ["设置"] = new(
                "设置",
                "ms-settings:",
                "⚙",
                "系统",
                ShellIconService.GetStockIcon(ShellStockIconId.Settings, large: false)
                    ?? ShellIconService.GetSystemExecutableIcon("SystemSettings.exe", large: false),
                "Settings"),
            ["文件"] = new(
                "文件",
                userProfile,
                "▣",
                "系统",
                ShellIconService.GetIcon(userProfile, large: false)
                    ?? ShellIconService.GetStockIcon(ShellStockIconId.Folder, large: false),
                "Folder"),
            ["文档"] = new(
                "文档",
                documents,
                "≡",
                "文件夹",
                ShellIconService.GetIcon(documents, large: false)
                    ?? ShellIconService.GetStockIcon(ShellStockIconId.Folder, large: false),
                "Folder"),
            ["下载"] = new(
                "下载",
                downloads,
                "↓",
                "文件夹",
                ShellIconService.GetIcon(downloads, large: false)
                    ?? ShellIconService.GetStockIcon(ShellStockIconId.Folder, large: false),
                "Folder")
        };

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file);
                    if (!StartMenuExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                    var name = Path.GetFileNameWithoutExtension(file).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, file)) ?? string.Empty;
                    var category = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "."
                        ? "应用"
                        : relativeDirectory.Split(Path.DirectorySeparatorChar)[0];

                    items.TryAdd(name, new StartAppItem(
                        name,
                        file,
                        "◆",
                        category,
                        ShellIconService.GetIcon(file, large: false)
                            ?? ShellIconService.GetStockIcon(ShellStockIconId.Application, large: false),
                        "App"));
                }
            }
            catch
            {
                // A Start Menu entry can disappear while it is being indexed.
            }
        }

        return items.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(600)
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
}
