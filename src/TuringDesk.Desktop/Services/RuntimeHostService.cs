using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace TuringDesk.Desktop.Services;

public enum RuntimeStartReason
{
    AgentRequest,
    ModelConfiguration,
    HarnessConsole,
    McpTask,
    Explicit
}

/// <summary>
/// Owns the optional Node.js runtime process.
///
/// Desktop enhancement/search-idle mode does not call this service. Callers that
/// actually need Runtime acquire a short-lived lease; a lease prevents idle
/// shutdown until the operation/workbench releases it. If another process already
/// owns port 4317, TuringDesk reuses it but never terminates that external process.
/// </summary>
public static class RuntimeHostService
{
    private static readonly Uri HealthUri = new("http://127.0.0.1:4317/health");
    private static readonly Uri ShutdownUri = new("http://127.0.0.1:4317/v1/runtime/shutdown");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(700) };
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim StartupGate = new(1, 1);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(15);

    private static Process? _ownedProcess;
    private static Timer? _idleTimer;
    private static DateTime _lastActivityUtc = DateTime.UtcNow;
    private static int _activeLeases;
    private static int _idleCheckRunning;
    private static bool _workbenchOpen;
    private static bool _shutdownHookRegistered;

    public static bool HasOwnedRuntime
    {
        get
        {
            lock (Gate)
                return _ownedProcess is { HasExited: false };
        }
    }

    public static int ActiveLeaseCount => Volatile.Read(ref _activeLeases);

    /// <summary>
    /// Compatibility alias for older call sites. New code should use
    /// EnsureRuntimeStartedAsync or AcquireAsync so intent is explicit.
    /// </summary>
    public static Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
        EnsureRuntimeStartedAsync(RuntimeStartReason.Explicit, cancellationToken);

    public static async Task EnsureRuntimeStartedAsync(
        RuntimeStartReason reason = RuntimeStartReason.Explicit,
        CancellationToken cancellationToken = default)
    {
        MarkActivity(reason);
        if (await IsRuntimeReadyAsync(cancellationToken).ConfigureAwait(false)) return;

        await StartupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsRuntimeReadyAsync(cancellationToken).ConfigureAwait(false)) return;

            lock (Gate)
            {
                if (_ownedProcess is null || _ownedProcess.HasExited)
                {
                    _ownedProcess?.Dispose();
                    _ownedProcess = StartRuntimeProcess();
                    RegisterShutdownHook();
                    EnsureIdleTimerNoLock();
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsRuntimeReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    MarkActivity(reason);
                    return;
                }

                Process? process;
                lock (Gate) process = _ownedProcess;
                if (process is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"TuringDesk Runtime exited during startup (exit code {process.ExitCode}).");
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                "TuringDesk Runtime did not become ready on 127.0.0.1:4317 within 15 seconds.");
        }
        finally
        {
            StartupGate.Release();
        }
    }

    /// <summary>
    /// Acquire a Runtime-use lease. The returned lease must be disposed when the
    /// current chat/model/MCP/workbench operation no longer needs the process.
    /// </summary>
    public static async ValueTask<RuntimeLease> AcquireAsync(
        RuntimeStartReason reason,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activeLeases);
        MarkActivity(reason);
        try
        {
            await EnsureRuntimeStartedAsync(reason, cancellationToken).ConfigureAwait(false);
            return new RuntimeLease(reason);
        }
        catch
        {
            ReleaseLease(reason);
            throw;
        }
    }

    public static void NotifyWorkbenchOpened()
    {
        lock (Gate)
        {
            _workbenchOpen = true;
            _lastActivityUtc = DateTime.UtcNow;
            EnsureIdleTimerNoLock();
        }
    }

    public static void NotifyWorkbenchClosed()
    {
        lock (Gate)
        {
            _workbenchOpen = false;
            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    public static void MarkActivity(RuntimeStartReason reason = RuntimeStartReason.Explicit)
    {
        lock (Gate)
        {
            _lastActivityUtc = DateTime.UtcNow;
            if (_ownedProcess is { HasExited: false }) EnsureIdleTimerNoLock();
        }
    }

    /// <summary>
    /// Heartbeat probe only. It never starts Runtime.
    /// </summary>
    public static async Task<bool> IsRuntimeReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HealthUri);
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

    public static Task StopIdleRuntimeNowAsync(CancellationToken cancellationToken = default) =>
        StopOwnedProcessGracefullyAsync(force: false, cancellationToken);

    private static void ReleaseLease(RuntimeStartReason reason)
    {
        var remaining = Interlocked.Decrement(ref _activeLeases);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _activeLeases, 0);
        }
        MarkActivity(reason);
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

    private static void EnsureIdleTimerNoLock()
    {
        _idleTimer ??= new Timer(
            static _ => _ = CheckIdleAsync(),
            null,
            IdlePollInterval,
            IdlePollInterval);
    }

    private static async Task CheckIdleAsync()
    {
        if (Interlocked.Exchange(ref _idleCheckRunning, 1) != 0) return;
        try
        {
            Process? owned;
            DateTime lastActivity;
            bool workbenchOpen;
            lock (Gate)
            {
                owned = _ownedProcess;
                lastActivity = _lastActivityUtc;
                workbenchOpen = _workbenchOpen;
            }

            if (owned is null || owned.HasExited) return;
            if (workbenchOpen || Volatile.Read(ref _activeLeases) > 0) return;
            if (DateTime.UtcNow - lastActivity < IdleTimeout) return;

            await StopOwnedProcessGracefullyAsync(force: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _idleCheckRunning, 0);
        }
    }

    private static async Task StopOwnedProcessGracefullyAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        Process? process;
        lock (Gate)
        {
            process = _ownedProcess;
            if (process is null) return;
            if (!force && (_workbenchOpen || Volatile.Read(ref _activeLeases) > 0)) return;
        }

        if (!process.HasExited)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, ShutdownUri);
                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Runtime may already be exiting or its HTTP loop may be unhealthy.
            }

            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort idle reclamation.
                }
            }
        }

        lock (Gate)
        {
            if (!ReferenceEquals(_ownedProcess, process)) return;
            process.Dispose();
            _ownedProcess = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private static void RegisterShutdownHook()
    {
        if (_shutdownHookRegistered) return;
        _shutdownHookRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOwnedProcessImmediately();
    }

    private static void StopOwnedProcessImmediately()
    {
        lock (Gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
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

    public sealed class RuntimeLease : IDisposable, IAsyncDisposable
    {
        private readonly RuntimeStartReason _reason;
        private int _disposed;

        internal RuntimeLease(RuntimeStartReason reason)
        {
            _reason = reason;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            ReleaseLease(_reason);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RuntimeLayout(
        string NodeExecutable,
        string RuntimeEntry,
        string RuntimeWorkingDirectory,
        string InstallRoot);
}
