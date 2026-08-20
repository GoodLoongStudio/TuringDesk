using System.Globalization;
using System.Text.RegularExpressions;
using OpenAI;
using OpenAI.Chat;

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
/// Level 3 of the desktop search stack: a persistent, tool-free CLI-style AI chat.
/// Uses the official OpenAI .NET SDK with custom Base URL support for any
/// OpenAI-compatible provider (DeepSeek, Ollama, LM Studio, etc.).
/// Does NOT start Node, Harness, or any external process.
/// </summary>
public sealed class DesktopQuickAnswerService
{
    private const int MaxDirectQuestionLength = 16000;
    private const int MaxConversationMessages = 16;
    private const string HarnessMarker = "[[HARNESS_REQUIRED]]";

    private static readonly Regex FormulaPattern = new(
        @"^[\s=0-9\.\+\-\*\/\%\^\(\)]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _historyGate = new();
    private readonly List<ChatTurn> _history = new();
    private string? _conversationModelKey;

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
                "CLI 对话",
                "请输入你想搜索或询问的内容。");
        }

        if (TryCalculate(text, out var calculation))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                "本地计算",
                calculation);
        }

        if (text.Length > MaxDirectQuestionLength)
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresDeepProcessing,
                "CLI 无法处理这么长的输入",
                "这段内容超过了常驻 CLI 的单次输入范围。需要更大的工作上下文时再升级到 Harness。");
        }

        if (model is null || !model.IsAvailable || model.Settings is null)
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresModel,
                "CLI 需要 AI 模型",
                "请先配置一个可用模型。应用搜索、文件搜索和本地计算仍然可以使用。");
        }

        var settings = model.Settings;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.RequiresModel,
                "模型配置不完整",
                "请检查 Base URL 和模型 ID。普通对话不会因此自动启动 Harness。");
        }

        var modelKey = $"{settings.ProviderId}|{settings.BaseUrl}|{settings.Model}";
        EnsureConversationModel(modelKey);
        var intent = Classify(text);
        var systemPrompt = BuildSystemPrompt(intent);

        try
        {
            var reply = await CallChatAsync(
                settings,
                model.Credential,
                systemPrompt,
                text,
                intent == QuickIntent.Translate ? 512 : 1400,
                cancellationToken).ConfigureAwait(false);

            if (TryExtractHarnessEscalation(reply, out var reason))
            {
                return new DesktopQuickAnswerResult(
                    DesktopQuickAnswerDisposition.RequiresDeepProcessing,
                    "CLI 需要更高权限的 Agent 能力",
                    reason);
            }

            AppendTurn(text, reply);
            return new DesktopQuickAnswerResult(
                DesktopQuickAnswerDisposition.Answered,
                intent switch
                {
                    QuickIntent.Translate => "CLI · 翻译",
                    QuickIntent.Explain => "CLI · 解释",
                    _ => "CLI · 对话"
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
                DesktopQuickAnswerDisposition.Answered,
                "CLI 连接失败",
                $"{error.Message} 请检查当前模型配置或稍后重试。不会自动启动 Harness。");
        }
    }

    public void ResetConversation()
    {
        lock (_historyGate)
        {
            _history.Clear();
            _conversationModelKey = null;
        }
    }

    private static string BuildSystemPrompt(QuickIntent intent)
    {
        var task = intent switch
        {
            QuickIntent.Translate => "Translate according to the user's requested direction and keep the answer direct.",
            QuickIntent.Explain => "Explain the requested concept clearly in the user's language.",
            _ => "Answer conversationally and helpfully in the user's language."
        };

        return $"""
You are TuringDesk CLI, the persistent Level-3 conversational assistant inside the desktop search bar.
{task}
You have conversational memory from prior turns, but you have NO local tools and must not claim you changed the computer.
Do not escalate ordinary questions, explanations, writing, translation, coding advice, or model/network errors.
Only when the user's requested outcome truly requires operating Windows, inspecting local files not provided in chat, running commands, controlling applications, or multi-step Agent/tool execution, respond with this marker as the FIRST line:
{HarnessMarker}
Then briefly explain why Level 4 Harness is required.
Otherwise answer normally and never mention Harness.
""";
    }

    /// <summary>
    /// Uses the OpenAI .NET SDK with a custom endpoint to support any
    /// OpenAI-compatible provider. The SDK handles serialization, retries,
    /// and error mapping internally.
    /// </summary>
    private async Task<string> CallChatAsync(
        ModelSettings settings,
        string? credential,
        string systemPrompt,
        string userText,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.BaseUrl.Trim();
        if (!baseUrl.EndsWith('/')) baseUrl += "/";

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl)
        };

        var client = string.IsNullOrWhiteSpace(credential)
            ? new OpenAIClient(options)
            : new OpenAIClient(new ApiKeyCredential(credential.Trim()), options);

        var chatClient = client.GetChatClient(settings.Model);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Include conversation history for multi-turn context.
        IReadOnlyList<ChatTurn> history;
        lock (_historyGate)
            history = _history.ToArray();

        foreach (var turn in history)
        {
            if (turn.Role == "user")
                messages.Add(new UserChatMessage(turn.Content));
            else
                messages.Add(new AssistantChatMessage(turn.Content));
        }

        messages.Add(new UserChatMessage(userText));

        var chatOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxTokens,
            Temperature = 0.2f
        };

        var completion = await chatClient.CompleteChatAsync(messages, chatOptions, cancellationToken)
            .ConfigureAwait(false);

        var reply = completion.Value.Content.Count > 0
            ? completion.Value.Content[0].Text?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("模型返回了空内容。");

        return reply;
    }

    private void EnsureConversationModel(string modelKey)
    {
        lock (_historyGate)
        {
            if (string.Equals(_conversationModelKey, modelKey, StringComparison.Ordinal)) return;
            _conversationModelKey = modelKey;
            _history.Clear();
        }
    }

    private void AppendTurn(string userText, string assistantText)
    {
        lock (_historyGate)
        {
            _history.Add(new ChatTurn("user", userText));
            _history.Add(new ChatTurn("assistant", assistantText));
            while (_history.Count > MaxConversationMessages)
                _history.RemoveAt(0);
        }
    }

    private static bool TryExtractHarnessEscalation(string reply, out string reason)
    {
        reason = string.Empty;
        if (!reply.StartsWith(HarnessMarker, StringComparison.Ordinal)) return false;
        reason = reply[HarnessMarker.Length..].TrimStart('\r', '\n', ' ', ':', '：').Trim();
        if (string.IsNullOrWhiteSpace(reason))
            reason = "这个请求需要操作本机、调用工具或执行多步骤 Agent 工作流。";
        return true;
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

    private sealed record ChatTurn(string Role, string Content);

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
