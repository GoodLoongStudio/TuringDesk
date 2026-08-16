using System.Diagnostics;
using System.IO;

namespace TuringDesk.Desktop.Services;

public sealed record DesktopSurfaceItem(
    string Name,
    string Path,
    string Glyph,
    string Kind,
    string Subtitle,
    bool IsDirectory);

public sealed record StartAppItem(
    string Name,
    string Target,
    string Glyph,
    string Category);

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
                    var name = System.IO.Path.GetFileName(directory);
                    items.TryAdd(directory, new DesktopSurfaceItem(name, directory, "▣", "folder", "文件夹", true));
                }

                foreach (var file in Directory.EnumerateFiles(root))
                {
                    if (ShouldHide(file)) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    items.TryAdd(file, new DesktopSurfaceItem(
                        name,
                        file,
                        GlyphForExtension(extension),
                        extension.TrimStart('.'),
                        SubtitleForExtension(extension),
                        false));
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
        var items = new Dictionary<string, StartAppItem>(StringComparer.CurrentCultureIgnoreCase)
        {
            ["设置"] = new("设置", "ms-settings:", "⚙", "系统"),
            ["文件"] = new("文件", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "▣", "系统"),
            ["文档"] = new("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "≡", "文件夹"),
            ["下载"] = new("下载", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "↓", "文件夹")
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
                    var extension = System.IO.Path.GetExtension(file);
                    if (!StartMenuExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                    var name = System.IO.Path.GetFileNameWithoutExtension(file).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var relativeDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, file)) ?? string.Empty;
                    var category = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "."
                        ? "应用"
                        : relativeDirectory.Split(System.IO.Path.DirectorySeparatorChar)[0];

                    items.TryAdd(name, new StartAppItem(name, file, "◆", category));
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
