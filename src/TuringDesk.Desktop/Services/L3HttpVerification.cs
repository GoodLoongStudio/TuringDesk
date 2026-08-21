using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed record L3HttpVerificationResult(bool Success, string Detail);

/// <summary>
/// Real loopback verification for the L3 native HttpClient path. It deliberately
/// avoids Harness, Node, external network access and real provider credentials.
/// A tiny one-shot TCP server speaks enough HTTP/SSE to prove that TuringDesk sends
/// an OpenAI-compatible request and consumes a streaming answer end to end.
/// </summary>
public static class L3HttpVerification
{
    public static async Task<L3HttpVerificationResult> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = ServeOneStreamingResponseAsync(listener, timeout.Token);

            var settings = new ModelSettings(
                "openai-compatible",
                "direct",
                $"http://127.0.0.1:{port}/v1",
                "verify-model",
                true);

            var endpoint = L3ChatProviderClient.ResolveChatCompletionsEndpoint(settings);
            if (endpoint.AbsolutePath != "/v1/chat/completions")
                return new(false, $"endpoint normalization failed: {endpoint}");

            string? lastPartial = null;
            var provider = new L3ChatProviderClient();
            var reply = await provider.CompleteAsync(
                settings,
                "verify-key",
                "You are a verification assistant.",
                Array.Empty<L3ChatMessage>(),
                "hello",
                32,
                partial => lastPartial = partial,
                timeout.Token);

            var observed = await serverTask;
            if (!observed.Path.Equals("/v1/chat/completions", StringComparison.Ordinal))
                return new(false, $"unexpected request path: {observed.Path}");
            if (!observed.Authorization.Equals("Bearer verify-key", StringComparison.Ordinal))
                return new(false, "Bearer credential was not sent correctly");

            using var requestJson = JsonDocument.Parse(observed.Body);
            var root = requestJson.RootElement;
            if (!root.TryGetProperty("model", out var model) || model.GetString() != "verify-model")
                return new(false, "request model was not sent correctly");
            if (!root.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
                return new(false, "stream=true was not sent");
            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() < 2)
                return new(false, "request messages were missing");
            var finalMessage = messages[messages.GetArrayLength() - 1];
            if (!finalMessage.TryGetProperty("content", out var content) || content.GetString() != "hello")
                return new(false, "user message was not sent correctly");

            if (reply != "你好")
                return new(false, $"unexpected final streaming reply: {reply}");
            if (lastPartial != "你好")
                return new(false, $"streaming callback did not receive the final text: {lastPartial}");

            return new(true, "native HttpClient + Bearer + OpenAI-compatible request + SSE streaming passed");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<ObservedRequest> ServeOneStreamingResponseAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var network = client.GetStream();

        var headerBytes = new List<byte>(2048);
        var window = new Queue<byte>(4);
        while (true)
        {
            var buffer = new byte[1];
            var read = await network.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new InvalidOperationException("loopback client closed before HTTP headers completed");
            headerBytes.Add(buffer[0]);
            window.Enqueue(buffer[0]);
            while (window.Count > 4) window.Dequeue();
            if (window.Count == 4 && window.SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            if (headerBytes.Count > 64 * 1024) throw new InvalidOperationException("loopback HTTP headers were unexpectedly large");
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var path = requestParts.Length >= 2 ? requestParts[1] : string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var parsedLength)
            ? parsedLength
            : 0;
        var bodyBytes = new byte[contentLength];
        var offset = 0;
        while (offset < bodyBytes.Length)
        {
            var read = await network.ReadAsync(bodyBytes.AsMemory(offset), cancellationToken);
            if (read == 0) throw new InvalidOperationException("loopback client closed before HTTP body completed");
            offset += read;
        }

        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n" +
                           "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n" +
                           "data: [DONE]\n\n";
        var responseBody = Encoding.UTF8.GetBytes(sse);
        var responseHeader = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            $"Content-Length: {responseBody.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await network.WriteAsync(responseHeader, cancellationToken);
        await network.WriteAsync(responseBody, cancellationToken);
        await network.FlushAsync(cancellationToken);

        return new ObservedRequest(
            path,
            headers.TryGetValue("Authorization", out var authorization) ? authorization : string.Empty,
            Encoding.UTF8.GetString(bodyBytes));
    }

    private sealed record ObservedRequest(string Path, string Authorization, string Body);
}
