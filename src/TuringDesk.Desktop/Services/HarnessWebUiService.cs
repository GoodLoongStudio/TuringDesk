using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace TuringDesk.Desktop.Services;

public static class HarnessWebUiService
{
    private const int Port = 4319;
    private static readonly Uri WebUri = new($"http://127.0.0.1:{Port}/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(700) };
    private static readonly object Gate = new();
    private static Process? _process;
    private static Task<Uri>? _startupTask;
    private static bool _shutdownHookRegistered;

    public static Uri Url => WebUri;

    /// <summary>
    /// Starts the bundled official Harness process synchronously up to Process.Start
    /// and returns the readiness task. App.OnStartup calls this before MainWindow is
    /// constructed, so Harness boot overlaps the desktop UI instead of starting only
    /// after the user can already interact with TuringDesk.
    /// </summary>
    public static Task<Uri> StartEarly()
    {
        var modelStore = new ModelSettingsStore();
        var model = modelStore.Load();
        var apiKey = modelStore.LoadApiKey();
        HarnessModelBridgeService.Synchronize(model, apiKey);

        lock (Gate)
        {
            if (_process is { HasExited: false } && _startupTask is not null)
            {
                return _startupTask;
            }

            if (_process is null || _process.HasExited)
            {
                _process?.Dispose();
                _process = StartHarnessWebProcess(model);
                RegisterShutdownHook();
            }

            _startupTask = WaitUntilReadyAsync(CancellationToken.None);
            return _startupTask;
        }
    }

    public static async Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken)) return WebUri;

        Task<Uri> startup;
        lock (Gate)
        {
            if (_process is { HasExited: false } && _startupTask is not null)
            {
                startup = _startupTask;
            }
            else
            {
                startup = StartEarlyOutsideGate();
            }
        }

        return cancellationToken.CanBeCanceled
            ? await startup.WaitAsync(cancellationToken)
            : await startup;
    }

    /// <summary>
    /// Called by the single TuringDesk model configuration pipeline. Both settings
    /// and the API key are written to Harness's own hot-reloaded stores. No process
    /// restart is required: official settings/credentials providers publish the
    /// change and LLM adapters resolve credentials on the next request.
    /// </summary>
    public static async Task ApplyModelSettingsAsync(ModelSettings settings, string? apiKey, CancellationToken cancellationToken = default)
    {
        HarnessModelBridgeService.Synchronize(settings, apiKey);

        if (await IsReadyAsync(cancellationToken))
        {
            // Allow the file watchers' write-settle window to publish the update
            // before a caller immediately submits its next prompt.
            await Task.Delay(180, cancellationToken);
            return;
        }

        _ = await EnsureRunningAsync(cancellationToken);
    }

    private static Task<Uri> StartEarlyOutsideGate()
    {
        // Caller owns Gate. This helper avoids re-entering the same lock while
        // preserving the synchronous Process.Start behavior of StartEarly().
        var modelStore = new ModelSettingsStore();
        var model = modelStore.Load();
        var apiKey = modelStore.LoadApiKey();
        HarnessModelBridgeService.Synchronize(model, apiKey);

        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            _process = StartHarnessWebProcess(model);
            RegisterShutdownHook();
        }

        _startupTask = WaitUntilReadyAsync(CancellationToken.None);
        return _startupTask;
    }

    private static async Task<Uri> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await IsReadyAsync(cancellationToken)) return WebUri;
            }
            catch (Exception error)
            {
                lastError = error;
            }

            Process? process;
            lock (Gate) process = _process;
            if (process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"DeepSeek Harness WebUI exited before becoming ready (exit code {process.ExitCode}).",
                    lastError);
            }

            await Task.Delay(300, cancellationToken);
        }

        throw new TimeoutException("DeepSeek Harness WebUI did not become ready on 127.0.0.1:4319 within 20 seconds.", lastError);
    }

    private static async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WebUri);
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

    private static Process StartHarnessWebProcess(ModelSettings model)
    {
        var layout = ResolveRuntimeLayout();
        var harnessHome = HarnessModelBridgeService.HarnessHome;
        Directory.CreateDirectory(harnessHome);
        HarnessModelBridgeService.Synchronize(model);

        var patchPath = Path.Combine(harnessHome, "turingdesk-web.patch.yml");
        WriteTuringDeskWebPatch(patchPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = layout.NodeExecutable,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(layout.DshBin);
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--patch");
        startInfo.ArgumentList.Add(patchPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(Port.ToString());

        // DSH_HOME points at the shared official settings/credential store. API
        // keys are intentionally absent from the inherited process environment so
        // the Harness Models page remains writable and reflects the shared store.
        HarnessModelBridgeService.ApplyEnvironment(startInfo);
        startInfo.Environment["TURINGDESK_CAPABILITY_URL"] = "http://127.0.0.1:4318";
        startInfo.Environment["TURINGDESK_MCP_NODE"] = layout.NodeExecutable;
        startInfo.Environment["TURINGDESK_MCP_SERVER"] = layout.WindowsMcpServer;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch the bundled DeepSeek Harness WebUI process.");
        return process;
    }

    private static void RegisterShutdownHook()
    {
        if (_shutdownHookRegistered) return;
        _shutdownHookRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOwnedProcess();
    }

    private static void StopOwnedProcess()
    {
        lock (Gate) StopOwnedProcessNoLock();
    }

    private static void StopOwnedProcessNoLock()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Windows is already tearing the desktop process down.
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _startupTask = null;
        }
    }

    private static RuntimeLayout ResolveRuntimeLayout()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("TURINGDESK_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var explicitLayout = TryLayout(explicitRoot);
            if (explicitLayout is not null) return explicitLayout;
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var packagedRoot = Directory.GetParent(baseDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(packagedRoot))
        {
            var packagedLayout = TryLayout(packagedRoot);
            if (packagedLayout is not null) return packagedLayout;
        }

        var cursor = new DirectoryInfo(baseDirectory);
        while (cursor is not null)
        {
            var devDsh = Path.Combine(cursor.FullName, "runtime", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            var devMcp = Path.Combine(cursor.FullName, "runtime", "dist", "windows-mcp-server.js");
            if (File.Exists(devDsh) && File.Exists(devMcp))
            {
                return new RuntimeLayout("node", devDsh, devMcp);
            }
            cursor = cursor.Parent;
        }

        throw new FileNotFoundException(
            "The bundled DeepSeek Harness WebUI was not found. Expected runtime/app/node_modules/@deepseek-ai/dsh/lib/bin.js in the installed layout.");
    }

    private static RuntimeLayout? TryLayout(string installRoot)
    {
        var node = Path.Combine(installRoot, "runtime", "node", "node.exe");
        var dsh = Path.Combine(installRoot, "runtime", "app", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var mcp = Path.Combine(installRoot, "runtime", "app", "windows-mcp-server.js");
        return File.Exists(node) && File.Exists(dsh) && File.Exists(mcp)
            ? new RuntimeLayout(node, dsh, mcp)
            : null;
    }

    private static void WriteTuringDeskWebPatch(string path)
    {
        const string patch = """
- id: turingdesk-windows
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: turingdesk
    transport: stdio
    command: !!js process.env.TURINGDESK_MCP_NODE
    args:
      - !!js process.env.TURINGDESK_MCP_SERVER
    env:
      TURINGDESK_CAPABILITY_URL: !!js process.env.TURINGDESK_CAPABILITY_URL ?? 'http://127.0.0.1:4318'
    toolCallTimeoutMs: 60000
    failOnStartupError: false
    reconnect:
      enabled: true
      initialDelayMs: 500
      maxDelayMs: 10000
      maxAttempts: 8
""";
        File.WriteAllText(path, patch, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record RuntimeLayout(string NodeExecutable, string DshBin, string WindowsMcpServer);
}
