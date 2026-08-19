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
/// Level 3 search-bar answers. This service is intentionally isolated from the
/// heavy Agent process stack. Local arithmetic stays in-process; translation and
/// tiny explanations call the selected OpenAI-compatible model endpoint directly
/// with a very small response budget.
/// </summary>
public sealed class DesktopQuickAnswerService
{
    private static readonly Regex FormulaPattern = new(
        @"^[\s=0-9\.\+\-\*\/\%\^\(\)]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public async Task<DesktopQuickAnswerResult> TryAnswerAsync(
        string query,
        DesktopAiModelChoice? model,
        CancellationToken cancellationToken = default)
    {
        var text = query.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Deep(text);

        if (TryCalculate(text, out var calculation))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                "计算结果",
                calculation);
        }

        if (text.Length > 180)
            return Deep(text);

        var intent = Classify(text);
        if (intent == QuickIntent.None)
            return Deep(text);

        if (model is null || !model.IsAvailable || model.Settings is null)
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresModel,
                "需要 AI 模型",
                "这个轻量请求需要一个已配置模型。应用/文件搜索和计算仍可离线使用。也可以点击“深度处理”进入 Harness。" );
        }

        var settings = model.Settings;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresModel,
                "模型配置不完整",
                "请补充 Base URL 和模型 ID，或点击“深度处理”进入 Harness 工作台。" );
        }

        var systemPrompt = intent switch
        {
            QuickIntent.Translate => "You are the lightweight translation route of a desktop search bar. Follow the user's requested translation direction. Return only the translation, with no analysis or extra commentary.",
            QuickIntent.Explain => "You are the lightweight explanation route of a desktop search bar. Explain the requested keyword in at most three concise sentences, using the user's language. Do not perform multi-step planning or tool use.",
            _ => throw new InvalidOperationException("Unsupported quick intent.")
        };

        try
        {
            var reply = await CallCompatibleChatAsync(
                settings,
                model.Credential,
                systemPrompt,
                text,
                cancellationToken).ConfigureAwait(false);

            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                intent == QuickIntent.Translate ? "快速翻译" : "快速解释",
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
                "轻量直答未完成",
                $"{error.Message} 可以点击“深度处理”交给 Harness。" );
        }
    }

    private static async Task<string> CallCompatibleChatAsync(
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        string userText,
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
            temperature = 0.1,
            max_tokens = 256,
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

        return QuickIntent.None;
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

    private static DesktopQuickAnswerResult Deep(string query) => new(
        DesktopQuickAnswerDisposition.RequiresDeepProcessing,
        "需要深度处理",
        string.IsNullOrWhiteSpace(query)
            ? "请输入任务。"
            : "顶部搜索只做应用/文件检索、计算、翻译和极简解释。这个请求可以交给 Harness Agent 工作台继续处理。" );

    private enum QuickIntent
    {
        None,
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
