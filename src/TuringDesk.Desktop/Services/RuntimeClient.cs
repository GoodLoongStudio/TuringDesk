using System.Net.Http.Json;

namespace TuringDesk.Desktop.Services;

public sealed class RuntimeClient
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:4317/"),
        Timeout = TimeSpan.FromSeconds(60)
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
}

public sealed record RuntimeHealth(bool Ok, string Mode, string Version);
public sealed record ChatRequest(string Message);
public sealed record ChatResponse(string Reply);
