using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TuringDesk.Desktop.Services;

public enum DesktopQuickAnswerDisposition
{
    Answered,
    RequiresModel,
    RequiresDeepProcessing
}

public sealed record DesktopQuickAnswerResult(
    DesktopQuickAnswerDisposition Disposition,
    string Title,
    string Message);

/// <summary>
/// Lightweight search-bar answers. This service is intentionally isolated from the
/// heavy Agent process stack. Arithmetic stays in-process and all ordinary AI Q&A,
/// translation and keyword explanations call the selected OpenAI-compatible model
/// endpoint directly. Harness is only offered after this lightweight route cannot
/// complete the request or when the user explicitly asks for deep processing.
/// </summary>
public sealed class DesktopQuickAnswerService
{
    private const int MaxDirectQuestionLength = 6000;

    private static readonly Regex FormulaPattern = new(
        @"^[\s=0-9\.\+\-\*\/\%\^\(\)]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<DesktopQuickAnswerResult> TryAnswerAsync(
        string query,
        DesktopAiModelChoice? model,
        CancellationToken cancellationToken = default)
    {
        var text = query.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                "AI 直答",
                "请输入你想搜索或询问的内容。");
        }

        if (TryCalculate(text, out var calculation))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                "计算结果",
                calculation);
        }

        if (text.Length > MaxDirectQuestionLength)
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresDeepProcessing,
                "内容较长",
                "这段内容超出了顶部快速问答的输入范围。可以点击“深度处理”交给 Harness 工作台继续。" );
        }

        if (model is null || !model.IsAvailable || model.Settings is null)
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresModel,
                "需要 AI 模型",
                "请先在设置中配置一个可用模型。应用/文件搜索和本地计算仍可直接使用；Harness 只用于后续需要工具或 Agent 的复杂任务。" );
        }

        var settings = model.Settings;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresModel,
                "模型配置不完整",
                "请先补充 Base URL 和模型 ID。顶部搜索会优先使用普通 AI 直答，不会因为普通问答自动启动 Harness。" );
        }

        var intent = Classify(text);
        var systemPrompt = intent switch
        {
            QuickIntent.Translate =>
                "You are TuringDesk's lightweight translation assistant. Follow the user's requested translation direction. Return the translation directly, without analysis, tool calls, or agent planning.",
            QuickIntent.Explain =>
                "You are TuringDesk's lightweight desktop Q&A assistant. Explain the requested concept clearly and concisely in the user's language. Do not call tools and do not claim to have operated the computer.",
            _ =>
                "You are TuringDesk's lightweight desktop AI assistant. Answer the user's question directly and helpfully in the user's language. Keep the answer concise unless detail is useful. Do not call tools, browse local files, execute commands, or claim that you changed the computer. If the user asks for an action that actually requires operating the system, explain what would need to be done; the user can choose the separate Deep Processing/Harness action afterward."
        };

        try
        {
            var reply = await CallCompatibleChatAsync(
                settings,
                model.Credential,
                systemPrompt,
                text,
                intent == QuickIntent.Translate ? 384 : 1024,
                cancellationToken).ConfigureAwait(false);

            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                intent switch
                {
                    QuickIntent.Translate => "快速翻译",
                    QuickIntent.Explain => "AI 解释",
                    _ => "AI 直答"
                },
                reply);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresDeepProcessing,
                "普通 AI 问答未完成",
                $"{error.Message} 你可以重试，或点击“深度处理”交给 Harness。" );
        }
    }

    private static async Task<string> CallCompatibleChatAsync(
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        string userText,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.BaseUrl.Trim();
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        var endpoint = new Uri(new Uri(baseUrl, UriKind.Absolute), "chat/completions");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(credential))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());

        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText }
            },
            temperature = 0.2,
            max_tokens = maxTokens,
            stream = false
        });

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"模型接口返回 HTTP {(int)response.StatusCode}。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("模型没有返回可显示的内容。");
        }

        var reply = content.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("模型返回了空内容。");
        return reply;
    }

    private static QuickIntent Classify(string text)
    {
        var value = text.TrimStart();
        if (value.StartsWith("翻译", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("translate", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("译成", StringComparison.OrdinalIgnoreCase))
            return QuickIntent.Translate;

        if (value.StartsWith("解释", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("什么是", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("啥是", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("what is", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("define ", StringComparison.OrdinalIgnoreCase))
            return QuickIntent.Explain;

        return QuickIntent.General;
    }

    private static bool TryCalculate(string text, out string result)
    {
        result = string.Empty;
        var expression = text.Trim().TrimStart('=');
        if (expression.Length == 0 || expression.Length > 96 || !FormulaPattern.IsMatch(expression))
            return false;
        if (!expression.Any(char.IsDigit) || !expression.Any(character => "+-*/%^".Contains(character)))
            return false;

        try
        {
            var value = new ExpressionParser(expression).Parse();
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            result = value.ToString("G15", CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum QuickIntent
    {
        General,
        Translate,
        Explain
    }

    private sealed class ExpressionParser
    {
        private readonly string _text;
        private int _position;

        public ExpressionParser(string text) => _text = text;

        public double Parse()
        {
            var value = ParseAdditive();
            SkipSpaces();
            if (_position != _text.Length) throw new FormatException("Unexpected token.");
            return value;
        }

        private double ParseAdditive()
        {
            var value = ParseMultiplicative();
            while (true)
            {
                SkipSpaces();
                if (Take('+')) value += ParseMultiplicative();
                else if (Take('-')) value -= ParseMultiplicative();
                else return value;
            }
        }

        private double ParseMultiplicative()
        {
            var value = ParsePower();
            while (true)
            {
                SkipSpaces();
                if (Take('*')) value *= ParsePower();
                else if (Take('/')) value /= ParsePower();
                else if (Take('%')) value %= ParsePower();
                else return value;
            }
        }

        private double ParsePower()
        {
            var left = ParseUnary();
            SkipSpaces();
            return Take('^') ? Math.Pow(left, ParsePower()) : left;
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Take('+')) return ParseUnary();
            if (Take('-')) return -ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (Take('('))
            {
                var value = ParseAdditive();
                SkipSpaces();
                if (!Take(')')) throw new FormatException("Missing closing parenthesis.");
                return value;
            }

            var start = _position;
            while (_position < _text.Length &&
                   (char.IsDigit(_text[_position]) || _text[_position] == '.'))
                _position++;

            if (start == _position) throw new FormatException("Number expected.");
            var token = _text[start.._position];
            return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private bool Take(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected) return false;
            _position++;
            return true;
        }

        private void SkipSpaces()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }
    }
}
