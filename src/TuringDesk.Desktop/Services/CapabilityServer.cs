using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TuringDesk.Desktop.Services;

public sealed class CapabilityServer : IAsyncDisposable
{
    public const int DefaultPort = 4318;

    private static readonly HashSet<string> AllowedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "code",
        "terminal"
    };

    private readonly AppLauncher _launcher;
    private readonly WindowManager _windows;
    private readonly Action<string, string>? _activity;
    private WebApplication? _app;

    public CapabilityServer(AppLauncher launcher, WindowManager windows, Action<string, string>? activity = null)
    {
        _launcher = launcher;
        _windows = windows;
        _activity = activity;
    }

    public string BaseUrl => $"http://127.0.0.1:{DefaultPort}";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(BaseUrl);

        var app = builder.Build();
        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            version = "0.2.0",
            endpoint = "/v1/capabilities/execute"
        }));
        app.MapGet("/v1/capabilities", () => Results.Json(CapabilityCatalog.All));
        app.MapPost("/v1/capabilities/execute", ExecuteAsync);

        await app.StartAsync(cancellationToken);
        _app = app;
    }

    private async Task<IResult> ExecuteAsync(HttpRequest httpRequest)
    {
        CapabilityRequest? request;
        try
        {
            request = await httpRequest.ReadFromJsonAsync<CapabilityRequest>();
        }
        catch (Exception error)
        {
            return Results.Json(CapabilityResponse.Fail($"Invalid request: {error.Message}"), statusCode: 400);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Json(CapabilityResponse.Fail("Capability name is required."), statusCode: 400);
        }

        try
        {
            var result = await ExecuteCapabilityAsync(request.Name, request.Arguments);
            _activity?.Invoke("tool", request.Name);
            return Results.Json(CapabilityResponse.Success(result));
        }
        catch (ArgumentException error)
        {
            return Results.Json(CapabilityResponse.Fail(error.Message), statusCode: 400);
        }
        catch (Exception error)
        {
            _activity?.Invoke("tool", $"{request.Name} failed: {error.Message}");
            return Results.Json(CapabilityResponse.Fail(error.Message), statusCode: 500);
        }
    }

    private async Task<object?> ExecuteCapabilityAsync(string name, JsonElement arguments)
    {
        switch (name)
        {
            case "app.launch":
            {
                var app = RequireString(arguments, "app").ToLowerInvariant();
                if (!AllowedApps.Contains(app))
                {
                    throw new ArgumentException("v0.2 app.launch only allows: chrome, code, terminal.");
                }

                var launched = await _launcher.LaunchAsync(app);
                return new { app, launched };
            }
            case "window.list":
                return _windows.ListWindows();
            case "window.find":
                return _windows.Find(RequireString(arguments, "query"));
            case "window.focus":
                return new { focused = _windows.Focus(RequireString(arguments, "handle")) };
            case "window.move":
                return new
                {
                    moved = _windows.Move(
                        RequireString(arguments, "handle"),
                        RequireInt(arguments, "x"),
                        RequireInt(arguments, "y"))
                };
            case "window.resize":
                return new
                {
                    resized = _windows.Resize(
                        RequireString(arguments, "handle"),
                        RequireInt(arguments, "width"),
                        RequireInt(arguments, "height"))
                };
            case "window.tile":
                return new
                {
                    tiled = _windows.TileSideBySide(
                        RequireString(arguments, "leftHandle"),
                        RequireString(arguments, "rightHandle"))
                };
            default:
                throw new ArgumentException($"Unknown capability: {name}");
        }
    }

    private static string RequireString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static int RequireInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var number))
        {
            throw new ArgumentException($"Argument '{name}' must be an integer.");
        }

        return number;
    }

    public async ValueTask DisposeAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await app.StopAsync(stopCts.Token);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}

public sealed record CapabilityRequest(string Name, JsonElement Arguments);
public sealed record CapabilityResponse(bool Ok, object? Result, string? Error)
{
    public static CapabilityResponse Success(object? result) => new(true, result, null);
    public static CapabilityResponse Fail(string error) => new(false, null, error);
}

public sealed record CapabilityDescriptor(string Name, string Description, string Risk);

public static class CapabilityCatalog
{
    public static readonly CapabilityDescriptor[] All =
    {
        new("app.launch", "Launch one allow-listed desktop app alias: chrome, code, or terminal.", "low"),
        new("window.list", "List visible top-level windows excluding TuringDesk itself.", "read"),
        new("window.find", "Find a visible top-level window by title or process name.", "read"),
        new("window.focus", "Restore and focus a visible top-level window.", "low"),
        new("window.move", "Move a visible top-level window while keeping it on the work area.", "low"),
        new("window.resize", "Resize a visible top-level window within the work area.", "low"),
        new("window.tile", "Tile two visible top-level windows side by side.", "low")
    };
}
