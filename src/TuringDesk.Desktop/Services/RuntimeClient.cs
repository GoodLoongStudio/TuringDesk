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

    public async Task<RuntimeHealth?> GetHealthAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<RuntimeHealth>("health");
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> ChatAsync(string message)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/chat", new ChatRequest(message));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
            return body?.Reply;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RuntimeModelSettings?> ConfigureModelAsync(ModelSettings settings, string? apiKey)
    {
        try
        {
            var request = new RuntimeModelSettings(
                settings.ProviderId,
                settings.Mode,
                settings.BaseUrl,
                settings.Model,
                apiKey);
            using var response = await _http.PostAsJsonAsync("v1/config/model", request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RuntimeModelSettings>();
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> TestModelAsync()
    {
        try
        {
            using var response = await _http.PostAsync("v1/config/model/test", null);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ModelTestResponse>();
            return body?.Reply;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record RuntimeHealth(bool Ok, string Mode, string Version, RuntimeModelSettings? Model);
public sealed record ChatRequest(string Message);
public sealed record ChatResponse(string Reply);
public sealed record ModelTestResponse(bool Ok, string Reply);
public sealed record RuntimeModelSettings(string ProviderId, string Mode, string BaseUrl, string Model, string? Credential = null);
