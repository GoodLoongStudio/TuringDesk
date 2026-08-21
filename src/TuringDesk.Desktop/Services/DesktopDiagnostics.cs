using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Process-wide diagnostics for the desktop application.
///
/// This service deliberately never uploads anything itself. Production builds and
/// ordinary users only write local diagnostics. The repository quick-verify flow
/// may collect the redacted files and publish them through an authenticated gh CLI
/// on a dedicated test machine.
/// </summary>
internal static class DesktopDiagnostics
{
    private const long MaxLogBytes = 4 * 1024 * 1024;
    private const int MaxCrashArchives = 8;
    private static readonly object Gate = new();
    private static bool _initialized;

    private static readonly Regex BearerPattern = new(
        @"(?i)(bearer\s+)[A-Za-z0-9._~+/=-]{10,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiKeyPattern = new(
        @"(?i)((?:api[_-]?key|token|secret)\s*[:=]\s*[""']?)[^""'\s,;]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SkPattern = new(
        @"(?i)\bsk-[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuringDesk",
        "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "desktop.log");
    public static string CrashReportPath { get; } = Path.Combine(LogDirectory, "desktop-crash-latest.json");

    public static void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
        }

        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        application.Exit += (_, e) => Info("lifecycle.exit", $"exitCode={e.ApplicationExitCode}");
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Info(
            "diagnostics.ready",
            $"version={GetVersion()} framework={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}");
    }

    public static void Info(string category, string message) => Write("INFO", category, message, null);
    public static void Warn(string category, string message) => Write("WARN", category, message, null);
    public static void Error(string category, string message, Exception? error = null) => Write("ERROR", category, message, error);

    public static void Fatal(string source, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Write("FATAL", source, error.Message, error);
        WriteCrashReport(source, error);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Fatal("dispatcher.unhandled", e.Exception);
        // Do not set Handled=true. A corrupted desktop process must still terminate
        // so quick-verify can fail deterministically and report the crash.
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var error = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled non-Exception object: {e.ExceptionObject}");
        Fatal(e.IsTerminating ? "appdomain.terminating" : "appdomain.unhandled", error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Error("task.unobserved", e.Exception.Message, e.Exception);
        e.SetObserved();
    }

    private static void Write(string level, string category, string message, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                var cleanMessage = OneLine(Redact(message));
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | pid={Environment.ProcessId} | tid={Environment.CurrentManagedThreadId} | {level} | {category} | {cleanMessage}";
                if (error is not null)
                    line += $" | exception={OneLine(Redact(error.ToString()))}";

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never become another reason for the desktop to fail.
        }
    }

    private static void WriteCrashReport(string source, Exception error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var now = DateTimeOffset.Now;
                var report = new
                {
                    schemaVersion = 1,
                    timestamp = now,
                    timestampUtc = now.UtcDateTime,
                    processId = Environment.ProcessId,
                    threadId = Environment.CurrentManagedThreadId,
                    source,
                    exceptionType = error.GetType().FullName,
                    message = Redact(error.Message),
                    exception = Redact(error.ToString()),
                    version = GetVersion(),
                    framework = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    commandLine = Redact(Environment.CommandLine),
                    quickVerify = string.Equals(Environment.GetEnvironmentVariable("TURINGDESK_QUICK_VERIFY"), "1", StringComparison.Ordinal),
                    verifyCommit = Environment.GetEnvironmentVariable("TURINGDESK_VERIFY_COMMIT")
                };

                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                var archive = Path.Combine(LogDirectory, $"desktop-crash-{now:yyyyMMdd-HHmmssfff}-p{Environment.ProcessId}.json");
                File.WriteAllText(archive, json);

                var temp = CrashReportPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, CrashReportPath, overwrite: true);
                PruneCrashArchives();
            }
        }
        catch
        {
            // The line-oriented FATAL record is still useful if JSON persistence fails.
        }
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static string Redact(string? text)
    {
        var value = text ?? string.Empty;
        value = BearerPattern.Replace(value, "$1<REDACTED>");
        value = ApiKeyPattern.Replace(value, "$1<REDACTED>");
        value = SkPattern.Replace(value, "<REDACTED_KEY>");

        value = ReplacePath(value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<USERPROFILE>");
        value = ReplacePath(value, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<LOCALAPPDATA>");
        value = ReplacePath(value, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "<APPDATA>");
        return value;
    }

    private static string ReplacePath(string text, string path, string token) =>
        string.IsNullOrWhiteSpace(path)
            ? text
            : text.Replace(path, token, StringComparison.OrdinalIgnoreCase);

    private static string OneLine(string text) => text
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxLogBytes) return;

        var previous = LogPath + ".1";
        try
        {
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(LogPath, previous);
        }
        catch
        {
            // Continue appending if rotation is temporarily unavailable.
        }
    }

    private static void PruneCrashArchives()
    {
        try
        {
            var archives = Directory.EnumerateFiles(LogDirectory, "desktop-crash-*-p*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaxCrashArchives)
                .ToArray();
            foreach (var archive in archives)
                archive.Delete();
        }
        catch
        {
            // Retention is best-effort.
        }
    }
}
