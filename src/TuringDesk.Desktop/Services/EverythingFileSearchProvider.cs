using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// File search provider backed exclusively by voidtools Everything.
///
/// TuringDesk never crawls the user's disks. The packaged product carries a pinned
/// Everything + ES pair for the current architecture. A private "TuringDesk"
/// Everything instance is started in the background and ES performs bounded IPC
/// queries against that database. An existing system installation remains a
/// development fallback when packaged components are not present.
/// </summary>
internal sealed class EverythingFileSearchProvider : IDisposable
{
    internal const string EverythingVersion = "1.4.1.1032";
    internal const string EsVersion = "1.1.0.37";
    private const string PrivateInstanceName = "TuringDesk";

    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _everythingPath;
    private readonly string? _esPath;
    private readonly bool _usesPrivateBundledInstance;
    private Task? _initializationTask;
    private volatile bool _isReady;
    private volatile bool _initializationFinished;
    private string _status = "正在初始化 Everything…";
    private int _disposed;

    public EverythingFileSearchProvider()
    {
        var architecture = ArchitectureFolder();
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "Everything", architecture);
        var bundledEverything = Path.Combine(bundledRoot, "Everything.exe");
        var bundledEs = Path.Combine(bundledRoot, "es.exe");

        if (File.Exists(bundledEverything) && File.Exists(bundledEs))
        {
            _everythingPath = bundledEverything;
            _esPath = bundledEs;
            _usesPrivateBundledInstance = true;
            return;
        }

        _everythingPath = FindEverythingExecutable();
        _esPath = FindEsExecutable();
        _usesPrivateBundledInstance = false;
    }

    public bool IsReady => _isReady;
    public bool InitializationCompleted => _initializationFinished;
    public string ProviderName => $"Everything {EverythingVersion}";
    public string Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_startupGate)
        {
            _initializationTask ??= EnsureReadyCoreAsync(_lifetime.Token);
            return cancellationToken.CanBeCanceled
                ? _initializationTask.WaitAsync(cancellationToken)
                : _initializationTask;
        }
    }

    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        if (!await EnsureReadyAsync(cancellationToken).ConfigureAwait(false)) return Array.Empty<string>();

        limit = Math.Clamp(limit, 1, 16);
        using var process = CreateEsProcess();
        AddInstanceArgument(process.StartInfo);
        process.StartInfo.ArgumentList.Add("-timeout");
        process.StartInfo.ArgumentList.Add("750");
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(limit.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-full-path-and-name");
        process.StartInfo.ArgumentList.Add("/a-d"); // Files only.
        process.StartInfo.ArgumentList.Add(query);

        try
        {
            if (!process.Start()) return Array.Empty<string>();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return Array.Empty<string>();

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_isReady) return true;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return _isReady;
    }

    private async Task EnsureReadyCoreAsync(CancellationToken cancellationToken)
    {
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isReady) return;
            if (string.IsNullOrWhiteSpace(_everythingPath) || !File.Exists(_everythingPath) ||
                string.IsNullOrWhiteSpace(_esPath) || !File.Exists(_esPath))
            {
                _status = "Everything 组件未就绪";
                return;
            }

            _status = "正在启动 Everything 文件索引…";
            if (_usesPrivateBundledInstance)
                StartPrivateInstance();
            else
                StartSystemInstanceIfNeeded();

            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProbeAsync(cancellationToken).ConfigureAwait(false))
                {
                    _isReady = true;
                    _status = $"Everything {EverythingVersion} 已就绪";
                    return;
                }
                await Task.Delay(125, cancellationToken).ConfigureAwait(false);
            }

            _status = "Everything 启动超时";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status = "Everything 初始化已取消";
        }
        catch (Exception error)
        {
            _status = $"Everything 初始化失败：{error.Message}";
        }
        finally
        {
            _initializationFinished = true;
            _startupGate.Release();
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        using var process = CreateEsProcess();
        AddInstanceArgument(process.StartInfo);
        process.StartInfo.ArgumentList.Add("-timeout");
        process.StartInfo.ArgumentList.Add("250");
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("-full-path-and-name");
        process.StartInfo.ArgumentList.Add("*");

        try
        {
            if (!process.Start()) return false;
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            return false;
        }
    }

    private Process CreateEsProcess() => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = _esPath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };

    private void AddInstanceArgument(ProcessStartInfo startInfo)
    {
        if (!_usesPrivateBundledInstance) return;
        startInfo.ArgumentList.Add("-instance");
        startInfo.ArgumentList.Add(PrivateInstanceName);
    }

    private void StartPrivateInstance()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuringDesk",
            "Everything");
        Directory.CreateDirectory(dataRoot);
        var configPath = Path.Combine(dataRoot, "Everything.ini");
        EnsurePrivateConfig(configPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _everythingPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_everythingPath!)!
        };
        startInfo.ArgumentList.Add("-startup");
        startInfo.ArgumentList.Add("-instance");
        startInfo.ArgumentList.Add(PrivateInstanceName);
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(configPath);
        _ = Process.Start(startInfo);
    }

    private void StartSystemInstanceIfNeeded()
    {
        if (FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null) != IntPtr.Zero) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = _everythingPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_everythingPath!)!
        };
        startInfo.ArgumentList.Add("-startup");
        _ = Process.Start(startInfo);
    }

    private static void EnsurePrivateConfig(string configPath)
    {
        // Portable Everything stores this private instance under TuringDesk's own
        // LocalAppData folder. No search window, tray icon or auto-update UI is
        // needed because TuringDesk owns the component version lifecycle.
        var desired = string.Join(Environment.NewLine,
        [
            "app_data=0",
            "run_as_admin=0",
            "run_in_background=1",
            "show_tray_icon=0",
            "allow_multiple_windows=0",
            "check_for_updates_on_startup=0",
            string.Empty
        ]);

        try
        {
            if (!File.Exists(configPath) || !string.Equals(File.ReadAllText(configPath), desired, StringComparison.Ordinal))
                File.WriteAllText(configPath, desired);
        }
        catch
        {
            // Everything can still start with its defaults if the config cannot be
            // persisted; readiness probing decides whether the backend is usable.
        }
    }

    private static string ArchitectureFolder() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        _ => Environment.Is64BitProcess ? "x64" : "x86"
    };

    private static string? FindEverythingExecutable()
    {
        var candidates = new List<string>();
        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe");
        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe");
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindEsExecutable()
    {
        var candidates = new List<string>();
        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe");
        AddIfRooted(candidates, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "es.exe");

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try { candidates.Add(Path.Combine(directory, "es.exe")); } catch { }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddIfRooted(List<string> candidates, string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var path = root;
            foreach (var part in parts) path = Path.Combine(path, part);
            candidates.Add(path);
        }
        catch { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();

        if (_usesPrivateBundledInstance && !string.IsNullOrWhiteSpace(_everythingPath) && File.Exists(_everythingPath))
        {
            try
            {
                var exitInfo = new ProcessStartInfo
                {
                    FileName = _everythingPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                exitInfo.ArgumentList.Add("-instance");
                exitInfo.ArgumentList.Add(PrivateInstanceName);
                exitInfo.ArgumentList.Add("-exit");
                _ = Process.Start(exitInfo);
            }
            catch { }
        }

        _lifetime.Dispose();
        _startupGate.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
}
