using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Lightweight provider connectivity probe used by settings. This path must stay
/// independent from RuntimeHostService and HarnessWebUiService so testing a model
/// never wakes the legacy 4317 Runtime or the full 4319 workbench.
/// </summary>
public sealed class ModelConnectionProbeService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<string> ProbeAsync(
        ModelSettings settings,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (settings.ProviderId.Equals("mock", StringComparison.OrdinalIgnoreCase) ||
            settings.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            return "Mock 模式无需网络连接。";
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            throw new InvalidOperationException("Base URL 为空。");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("模型 ID 为空。");

        var baseUrl = settings.BaseUrl.Trim();
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var endpoint = new Uri(new Uri(baseUrl, UriKind.Absolute), "chat/completions");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new object[]
            {
                new { role = "user", content = "Reply with OK only." }
            },
            temperature = 0,
            max_tokens = 8,
            stream = false
        });

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"模型接口返回 HTTP {(int)response.StatusCode}。"
                    : $"模型接口返回 HTTP {(int)response.StatusCode}：{detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("模型接口已连接，但没有返回 OpenAI-compatible choices。请检查 Base URL 和模型类型。");
        }

        return $"{settings.Model} 可用";
    }

    private static async Task<string?> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message))
                    return message.GetString();
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
            }
        }
        catch
        {
            // Error bodies are provider-specific. The HTTP status remains useful.
        }
        return null;
    }
}
