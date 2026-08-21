using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed record L3ChatMessage(string Role, string Content);

/// <summary>
/// Provider transport for the tool-free Level-3 conversation layer.
/// It follows the OpenAI-compatible streaming contract used by DeepSeek,
/// Ollama, LM Studio and relays, while keeping provider errors visible.
/// </summary>
public sealed class L3ChatProviderClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public async Task<string> CompleteAsync(
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        IReadOnlyList<L3ChatMessage> history,
        string userText,
        int maxTokens,
        Action<string>? onPartial = null,
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
                    maxTokens,
                    stream: true);

                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return await ReadStreamingAssistantTextAsync(response, onPartial, cancellationToken).ConfigureAwait(false);

                var error = await ReadProviderErrorAsync(response, cancellationToken).ConfigureAwait(false);
                if (attempt == 0 && ShouldRetry(response.StatusCode))
                {
                    lastError = new InvalidOperationException(error);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
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

        return new Uri(raw.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
    }

    private static HttpRequestMessage BuildRequest(
        Uri endpoint,
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        IReadOnlyList<L3ChatMessage> history,
        string userText,
        int maxTokens,
        bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(credential))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var messages = new List<object>(history.Count + 2)
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var turn in history)
            messages.Add(new { role = turn.Role, content = turn.Content });
        messages.Add(new { role = "user", content = userText });

        // DeepSeek V4 enables high-effort thinking by default. L3 is deliberately
        // the fast, tool-free CLI tier, so disable thinking here. L4 Harness remains
        // the explicit place for deeper Agent reasoning and tool execution.
        request.Content = settings.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            ? JsonContent.Create(new
            {
                model = settings.Model.Trim(),
                messages,
                temperature = 0.2,
                max_tokens = maxTokens,
                stream,
                thinking = new { type = "disabled" }
            })
            : JsonContent.Create(new
            {
                model = settings.Model.Trim(),
                messages,
                temperature = 0.2,
                max_tokens = maxTokens,
                stream
            });
        return request;
    }

    private static async Task<string> ReadStreamingAssistantTextAsync(
        HttpResponseMessage response,
        Action<string>? onPartial,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return await ReadJsonAssistantTextAsync(response, onPartial, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var accumulated = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

            var delta = ExtractStreamDelta(payload);
            if (string.IsNullOrEmpty(delta)) continue;
            accumulated.Append(delta);
            onPartial?.Invoke(accumulated.ToString());
        }

        var reply = accumulated.ToString().Trim();
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("模型建立了流式连接，但没有返回可显示内容。");
        return reply;
    }

    private static async Task<string> ReadJsonAssistantTextAsync(
        HttpResponseMessage response,
        Action<string>? onPartial,
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
        onPartial?.Invoke(reply);
        return reply;
    }

    private static string ExtractStreamDelta(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("delta", out var delta) ||
                !delta.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
                return string.Empty;

            return content.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
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
