using System.IO;
using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services.AppSearch;

internal sealed record DiscoveredProgram(
    string Name,
    string Target,
    string Category,
    string Source,
    IReadOnlyList<string> AlternateNames);

/// <summary>
/// Program discovery layer modelled after the mature PowerToys Run approach:
/// classic Win32 shortcuts are discovered from Windows shell roots, while packaged
/// applications are enumerated from shell:AppsFolder and retained by AUMID.
/// No executable-drive crawl is performed.
/// </summary>
internal sealed class ProgramDiscoveryService
{
    private static readonly HashSet<string> LaunchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".appref-ms"
    };

    public IReadOnlyList<string> WatchRoots => BuildLaunchRoots()
        .Select(root => root.Path)
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<DiscoveredProgram> Discover()
    {
        var programs = new Dictionary<string, DiscoveredProgram>(StringComparer.OrdinalIgnoreCase);

        AddBuiltIns(programs);
        DiscoverClassicShortcuts(programs);
        DiscoverPackagedApps(programs);

        return programs.Values
            .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void AddBuiltIns(Dictionary<string, DiscoveredProgram> programs)
    {
        Add(programs, new DiscoveredProgram(
            "设置",
            "ms-settings:",
            "系统",
            "System",
            ["settings", "system settings", "windows settings"]));

        Add(programs, new DiscoveredProgram(
            "文件资源管理器",
            "explorer.exe",
            "系统",
            "System",
            ["explorer", "file explorer", "文件", "资源管理器"]));

        Add(programs, new DiscoveredProgram(
            "任务管理器",
            "taskmgr.exe",
            "系统",
            "System",
            ["task manager", "taskmgr"]));
    }

    private static void DiscoverClassicShortcuts(Dictionary<string, DiscoveredProgram> programs)
    {
        foreach (var root in BuildLaunchRoots())
        {
            if (!Directory.Exists(root.Path)) continue;

            try
            {
                var option = root.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(root.Path, "*.*", option))
                {
                    var extension = Path.GetExtension(file);
                    if (!LaunchExtensions.Contains(extension) || ShouldHide(file)) continue;

                    var name = Path.GetFileNameWithoutExtension(file).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var category = root.Label;
                    if (root.Recursive && root.IsStartMenu)
                    {
                        try
                        {
                            var relative = Path.GetRelativePath(root.Path, file);
                            var relativeDirectory = Path.GetDirectoryName(relative);
                            if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
                                category = relativeDirectory.Split(Path.DirectorySeparatorChar)[0];
                        }
                        catch { }
                    }

                    Add(programs, new DiscoveredProgram(
                        name,
                        file,
                        category,
                        root.Label,
                        [Path.GetFileNameWithoutExtension(file)]));
                }
            }
            catch
            {
                // Start menu and pinned shortcut trees can mutate during install or
                // update. A later refresh rebuilds the immutable snapshot.
            }
        }
    }

    private static void DiscoverPackagedApps(Dictionary<string, DiscoveredProgram> programs)
    {
        object? shell = null;
        object? folder = null;
        object? items = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            dynamic shellDynamic = shell;
            folder = shellDynamic.NameSpace("shell:AppsFolder");
            if (folder is null) return;

            dynamic folderDynamic = folder;
            items = folderDynamic.Items();
            if (items is null) return;

            dynamic itemsDynamic = items;
            var count = Convert.ToInt32(itemsDynamic.Count);
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = itemsDynamic.Item(index);
                    if (item is null) continue;

                    dynamic app = item;
                    var name = Convert.ToString(app.Name)?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var appUserModelId = Convert.ToString(app.ExtendedProperty("System.AppUserModel.ID"))?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(appUserModelId)) continue;

                    Add(programs, new DiscoveredProgram(
                        name,
                        "aumid:" + appUserModelId,
                        "Windows 应用",
                        "AppsFolder",
                        [appUserModelId]));
                }
                catch
                {
                    // One malformed shell item must not invalidate the catalogue.
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        catch
        {
            // Packaged app enumeration is additive. Classic Win32 discovery remains
            // functional if Explorer's AppsFolder COM surface is unavailable.
        }
        finally
        {
            ReleaseCom(items);
            ReleaseCom(folder);
            ReleaseCom(shell);
        }
    }

    private static IReadOnlyList<LaunchRoot> BuildLaunchRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            new(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "开始菜单", Recursive: true, IsStartMenu: true),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "公共开始菜单", Recursive: true, IsStartMenu: true),
            new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面快捷方式", Recursive: false, IsStartMenu: false),
            new(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面快捷方式", Recursive: false, IsStartMenu: false),
            new(Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"), "任务栏固定", Recursive: true, IsStartMenu: false)
        ];
    }

    private static void Add(Dictionary<string, DiscoveredProgram> programs, DiscoveredProgram program)
    {
        var targetKey = program.Target.Trim();
        if (targetKey.Length == 0) return;

        // AUMID/shortcut target is the stable identity. Prefer AppsFolder metadata
        // for packaged apps, otherwise retain the first shell-visible shortcut.
        if (!programs.TryGetValue(targetKey, out var existing) ||
            (program.Source == "AppsFolder" && existing.Source != "AppsFolder"))
        {
            programs[targetKey] = program;
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

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { _ = Marshal.FinalReleaseComObject(value); } catch { }
    }

    private sealed record LaunchRoot(string Path, string Label, bool Recursive, bool IsStartMenu);
}
