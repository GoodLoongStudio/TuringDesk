using System.Net.Http;
using System.Net.Http.Json;

namespace TuringDesk.Desktop.Services;

public sealed class RuntimeClient
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:4317/"),
        Timeout = TimeSpan.FromSeconds(120)
    };

    /// <summary>
    /// Passive heartbeat. It never wakes a sleeping Runtime.
    /// </summary>
    public async Task<RuntimeHealth?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<RuntimeHealth>("health", cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Passive state probe. Polling UI must not accidentally spawn Node/Harness.
    /// </summary>
    public async Task<AgentActivityState?> GetAgentStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AgentActivityState>("v1/agent/state", cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> ChatAsync(string message, CancellationToken cancellationToken = default)
    {
        await using var lease = await RuntimeHostService.AcquireAsync(
            RuntimeStartReason.AgentRequest,
            cancellationToken);

        try
        {
            using var response = await _http.PostAsJsonAsync("v1/chat", new ChatRequest(message), cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: cancellationToken);
            RuntimeHostService.MarkActivity(RuntimeStartReason.AgentRequest);
            return body?.Reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RuntimeModelSettings?> ConfigureModelAsync(
        ModelSettings settings,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await RuntimeHostService.AcquireAsync(
            RuntimeStartReason.ModelConfiguration,
            cancellationToken);

        try
        {
            var request = new RuntimeModelSettings(
                settings.ProviderId,
                settings.Mode,
                settings.BaseUrl,
                settings.Model,
                apiKey);
            using var response = await _http.PostAsJsonAsync("v1/config/model", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var configured = await response.Content.ReadFromJsonAsync<RuntimeModelSettings>(cancellationToken: cancellationToken);
            RuntimeHostService.MarkActivity(RuntimeStartReason.ModelConfiguration);
            return configured;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> TestModelAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await RuntimeHostService.AcquireAsync(
            RuntimeStartReason.ModelConfiguration,
            cancellationToken);

        try
        {
            using var response = await _http.PostAsync("v1/config/model/test", null, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ModelTestResponse>(cancellationToken: cancellationToken);
            RuntimeHostService.MarkActivity(RuntimeStartReason.ModelConfiguration);
            return body?.Reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record RuntimeHealth(bool Ok, string Mode, string Version, RuntimeModelSettings? Model);
public sealed record ChatRequest(string Message);
public sealed record ChatResponse(string Reply, int? RunId = null);
public sealed record ModelTestResponse(bool Ok, string Reply);
public sealed record RuntimeModelSettings(string ProviderId, string Mode, string BaseUrl, string Model, string? Credential = null);

public sealed record AgentTraceItem(
    DateTimeOffset At,
    string Kind,
    string Text);

public sealed record AgentRunHistory(
    int Id,
    string Prompt,
    string Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? ReplyPreview,
    string? Error,
    IReadOnlyList<AgentTraceItem> Trace);

public sealed record AgentActivityState(
    string Phase,
    bool Busy,
    int RunId,
    string? CurrentPrompt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ReplyPreview,
    string? Error,
    IReadOnlyList<AgentTraceItem> Trace,
    IReadOnlyList<AgentRunHistory> History,
    string Mode);
