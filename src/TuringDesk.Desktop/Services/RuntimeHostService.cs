using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace TuringDesk.Desktop.Services;

public static class RuntimeHostService
{
    private static readonly Uri HealthUri = new("http://127.0.0.1:4317/health");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(700) };
    private static readonly object Gate = new();
    private static Process? _ownedProcess;
    private static bool _shutdownHookRegistered;

    public static async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken)) return;

        lock (Gate)
        {
            if (_ownedProcess is null || _ownedProcess.HasExited)
            {
                _ownedProcess?.Dispose();
                _ownedProcess = StartRuntimeProcess();
                RegisterShutdownHook();
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsReadyAsync(cancellationToken)) return;

            Process? process;
            lock (Gate) process = _ownedProcess;
            if (process is { HasExited: true })
            {
                throw new InvalidOperationException($"TuringDesk Runtime exited during startup (exit code {process.ExitCode}).");
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("TuringDesk Runtime did not become ready on 127.0.0.1:4317 within 15 seconds.");
    }

    private static async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HealthUri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static Process StartRuntimeProcess()
    {
        var layout = ResolveRuntimeLayout();
        var startInfo = new ProcessStartInfo
        {
            FileName = layout.NodeExecutable,
            WorkingDirectory = layout.RuntimeWorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(layout.RuntimeEntry);
        startInfo.Environment["TURINGDESK_RUNTIME_MODE"] = "mock";
        startInfo.Environment["TURINGDESK_INSTALL_ROOT"] = layout.InstallRoot;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch the embedded TuringDesk Runtime.");
    }

    private static RuntimeLayout ResolveRuntimeLayout()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("TURINGDESK_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var explicitLayout = TryPackagedLayout(explicitRoot);
            if (explicitLayout is not null) return explicitLayout;
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var packagedRoot = Directory.GetParent(baseDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(packagedRoot))
        {
            var packagedLayout = TryPackagedLayout(packagedRoot);
            if (packagedLayout is not null) return packagedLayout;
        }

        var cursor = new DirectoryInfo(baseDirectory);
        while (cursor is not null)
        {
            var runtimeRoot = Path.Combine(cursor.FullName, "runtime");
            var devEntry = Path.Combine(runtimeRoot, "dist", "server.js");
            if (File.Exists(devEntry))
            {
                return new RuntimeLayout(
                    "node",
                    devEntry,
                    runtimeRoot,
                    cursor.FullName);
            }
            cursor = cursor.Parent;
        }

        throw new FileNotFoundException(
            "The bundled TuringDesk Runtime was not found. Expected runtime/node/node.exe and runtime/app/server.js in the installed layout.");
    }

    private static RuntimeLayout? TryPackagedLayout(string installRoot)
    {
        var node = Path.Combine(installRoot, "runtime", "node", "node.exe");
        var entry = Path.Combine(installRoot, "runtime", "app", "server.js");
        var working = Path.Combine(installRoot, "runtime");
        return File.Exists(node) && File.Exists(entry)
            ? new RuntimeLayout(node, entry, working, installRoot)
            : null;
    }

    private static void RegisterShutdownHook()
    {
        if (_shutdownHookRegistered) return;
        _shutdownHookRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOwnedProcess();
    }

    private static void StopOwnedProcess()
    {
        lock (Gate)
        {
            if (_ownedProcess is null) return;
            try
            {
                if (!_ownedProcess.HasExited) _ownedProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process teardown is best-effort during application shutdown.
            }
            finally
            {
                _ownedProcess.Dispose();
                _ownedProcess = null;
            }
        }
    }

    private sealed record RuntimeLayout(
        string NodeExecutable,
        string RuntimeEntry,
        string RuntimeWorkingDirectory,
        string InstallRoot);
}
