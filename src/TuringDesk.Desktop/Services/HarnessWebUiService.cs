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
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(15);

    private static Process? _process;
    private static Task<Uri>? _startupTask;
    private static Timer? _idleTimer;
    private static DateTime _lastUseUtc = DateTime.UtcNow;
    private static int _activeConsoles;
    private static int _idleCheckRunning;
    private static bool _shutdownHookRegistered;

    public static Uri Url => WebUri;

    /// <summary>
    /// Kept for explicit advanced callers. Desktop startup no longer invokes it.
    /// </summary>
    public static Task<Uri> StartEarly()
    {
        var modelStore = new ModelSettingsStore();
        var model = modelStore.Load();
        var apiKey = modelStore.LoadApiKey();
        HarnessModelBridgeService.Synchronize(model, apiKey);

        lock (Gate)
        {
            TouchNoLock();
            if (_process is { HasExited: false } && _startupTask is not null)
                return _startupTask;

            if (_process is null || _process.HasExited)
            {
                _process?.Dispose();
                _process = StartHarnessWebProcess(model);
                RegisterShutdownHook();
            }

            EnsureIdleTimerNoLock();
            _startupTask = WaitUntilReadyAsync(CancellationToken.None);
            return _startupTask;
        }
    }

    public static async Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        Touch();
        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false)) return WebUri;

        Task<Uri> startup;
        lock (Gate)
        {
            TouchNoLock();
            if (_process is { HasExited: false } && _startupTask is not null)
                startup = _startupTask;
            else
                startup = StartEarlyOutsideGate();
        }

        return cancellationToken.CanBeCanceled
            ? await startup.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await startup.ConfigureAwait(false);
    }

    public static void NotifyConsoleOpened()
    {
        Interlocked.Increment(ref _activeConsoles);
        lock (Gate)
        {
            TouchNoLock();
            EnsureIdleTimerNoLock();
        }
    }

    public static void NotifyConsoleClosed()
    {
        var remaining = Interlocked.Decrement(ref _activeConsoles);
        if (remaining < 0) Interlocked.Exchange(ref _activeConsoles, 0);
        Touch();
    }

    /// <summary>
    /// Synchronize the shared official Harness stores without waking a cold WebUI.
    /// If the console is already running, give its file watchers a brief settle
    /// window; otherwise the next explicit EnsureRunningAsync reads the new stores.
    /// </summary>
    public static async Task ApplyModelSettingsAsync(
        ModelSettings settings,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        HarnessModelBridgeService.Synchronize(settings, apiKey);

        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            Touch();
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<Uri> StartEarlyOutsideGate()
    {
        var modelStore = new ModelSettingsStore();
        var model = modelStore.Load();
        var apiKey = modelStore.LoadApiKey();
        HarnessModelBridgeService.Synchronize(model, apiKey);

        TouchNoLock();
        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            _process = StartHarnessWebProcess(model);
            RegisterShutdownHook();
        }

        EnsureIdleTimerNoLock();
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
                if (await IsReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    Touch();
                    return WebUri;
                }
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

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "DeepSeek Harness WebUI did not become ready on 127.0.0.1:4319 within 20 seconds.",
            lastError);
    }

    private static async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WebUri);
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
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

        HarnessModelBridgeService.ApplyEnvironment(startInfo);
        startInfo.Environment["TURINGDESK_CAPABILITY_URL"] = "http://127.0.0.1:4318";
        startInfo.Environment["TURINGDESK_MCP_NODE"] = layout.NodeExecutable;
        startInfo.Environment["TURINGDESK_MCP_SERVER"] = layout.WindowsMcpServer;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch the bundled DeepSeek Harness WebUI process.");
    }

    private static void Touch()
    {
        lock (Gate) TouchNoLock();
    }

    private static void TouchNoLock() => _lastUseUtc = DateTime.UtcNow;

    private static void EnsureIdleTimerNoLock()
    {
        _idleTimer ??= new Timer(
            static _ => _ = CheckIdleAsync(),
            null,
            IdlePollInterval,
            IdlePollInterval);
    }

    private static Task CheckIdleAsync()
    {
        if (Interlocked.Exchange(ref _idleCheckRunning, 1) != 0)
            return Task.CompletedTask;

        try
        {
            lock (Gate)
            {
                if (_process is null || _process.HasExited) return Task.CompletedTask;
                if (Volatile.Read(ref _activeConsoles) > 0) return Task.CompletedTask;
                if (DateTime.UtcNow - _lastUseUtc < IdleTimeout) return Task.CompletedTask;
                StopOwnedProcessNoLock();
            }
        }
        finally
        {
            Volatile.Write(ref _idleCheckRunning, 0);
        }

        return Task.CompletedTask;
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
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Windows may already be tearing the desktop process down.
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
                return new RuntimeLayout("node", devDsh, devMcp);
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
