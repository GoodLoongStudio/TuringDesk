using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed record L3ChatMessage(string Role, string Content);

/// <summary>
/// Provider transport for the tool-free Level-3 conversation layer.
/// Keeps provider quirks out of the search UI and exposes useful API errors
/// instead of collapsing every failure into an HTTP status code.
/// </summary>
public sealed class L3ChatProviderClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public async Task<string> CompleteAsync(
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        IReadOnlyList<L3ChatMessage> history,
        string userText,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveChatCompletionsEndpoint(settings);
        Exception? lastError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = BuildRequest(
                    endpoint,
                    settings,
                    credential,
                    systemPrompt,
                    history,
                    userText,
                    maxTokens);

                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return await ReadAssistantTextAsync(response, cancellationToken).ConfigureAwait(false);

                var error = await ReadProviderErrorAsync(response, cancellationToken).ConfigureAwait(false);
                if (attempt == 0 && ShouldRetry(response.StatusCode))
                {
                    lastError = new InvalidOperationException(error);
                    await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new InvalidOperationException(error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException error) when (attempt == 0)
            {
                lastError = error;
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"无法连接模型服务 {endpoint.Host}：{lastError?.Message ?? "未知网络错误"}",
            lastError);
    }

    public static Uri ResolveChatCompletionsEndpoint(ModelSettings settings)
    {
        var raw = settings.BaseUrl.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("Base URL 不是有效的绝对地址。");

        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return baseUri;

        // Preserve an explicit /v1 supplied by Ollama, LM Studio or a relay.
        // DeepSeek's configured root intentionally remains /chat/completions.
        var suffix = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "chat/completions"
            : "chat/completions";

        var normalized = raw.TrimEnd('/') + "/" + suffix;
        return new Uri(normalized, UriKind.Absolute);
    }

    private static HttpRequestMessage BuildRequest(
        Uri endpoint,
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        IReadOnlyList<L3ChatMessage> history,
        string userText,
        int maxTokens)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(credential))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var messages = new List<object>(history.Count + 2)
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var turn in history)
            messages.Add(new { role = turn.Role, content = turn.Content });
        messages.Add(new { role = "user", content = userText });

        request.Content = JsonContent.Create(new
        {
            model = settings.Model.Trim(),
            messages,
            temperature = 0.2,
            max_tokens = maxTokens,
            stream = false
        });
        return request;
    }

    private static async Task<string> ReadAssistantTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("模型响应格式不兼容：缺少 choices[0].message.content。");
        }

        var reply = content.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("模型返回了空内容。");
        return reply;
    }

    private static async Task<string> ReadProviderErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Keep the HTTP status even when an upstream closes the error body early.
        }

        var detail = ExtractErrorMessage(body);
        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"模型接口请求失败：{status}。"
            : $"模型接口请求失败：{status}。{detail}";
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return TrimError(error.GetString());
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                    return TrimError(message.GetString());
            }

            if (root.TryGetProperty("message", out var topMessage) && topMessage.ValueKind == JsonValueKind.String)
                return TrimError(topMessage.GetString());
        }
        catch (JsonException)
        {
            // Some relays return plain text or HTML. Show a small safe excerpt.
        }

        return TrimError(body);
    }

    private static string TrimError(string? value)
    {
        var text = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return text.Length <= 360 ? text : text[..360] + "…";
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;
}
