using System.Globalization;
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
/// Level 3 of the desktop search stack: TuringDesk's persistent local CLI / light
/// Agent layer. Bounded native tools are attempted first; ordinary language work
/// then falls back to the configured model. L3 never starts Node/Harness and the
/// explicit L3 -> L4 boundary remains owned by this service.
/// </summary>
public sealed class DesktopQuickAnswerService
{
    private const int MaxDirectQuestionLength = 16000;
    private const int MaxConversationMessages = 24;
    private const int MaxContextCharacters = 28000;
    private const string HarnessMarker = "[[HARNESS_REQUIRED]]";

    private static readonly Regex FormulaPattern = new(
        @"^[\s=0-9\.\+\-\*\/\%\^\(\)]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _historyGate = new();
    private readonly List<L3ChatMessage> _history = new();
    private readonly L3ChatProviderClient _provider = new();
    private readonly L3ConversationSessionStore _sessionStore = new();
    private readonly L3NativeToolService _nativeTools;
    private string? _conversationModelKey;

    public DesktopQuickAnswerService(DesktopSearchIndexService searchIndex)
    {
        _nativeTools = new L3NativeToolService(searchIndex);
    }

    /// <summary>
    /// Raised as the current model reply grows. Native CLI tool replies complete
    /// immediately and do not use this event.
    /// </summary>
    public event Action<string>? PartialResponseUpdated;

    public async Task<DesktopQuickAnswerResult> TryAnswerAsync(
        string query,
        DesktopAiModelChoice? model,
        CancellationToken cancellationToken = default,
        Action<string>? onPartial = null)
    {
        var text = query.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new(DesktopQuickAnswerDisposition.Answered, "CLI", "请输入你想搜索、查询、打开或询问的内容。输入 /help 可查看本地能力。");

        if (TryCalculate(text, out var calculation))
            return new(DesktopQuickAnswerDisposition.Answered, "CLI · 本地计算", calculation);

        if (text.Length > MaxDirectQuestionLength)
            return new(
                DesktopQuickAnswerDisposition.RequiresDeepProcessing,
                "CLI 输入过长",
                "这段内容超过常驻 CLI 的单次输入范围。需要更大工作上下文时再升级到 Harness。");

        var native = await _nativeTools.TryHandleAsync(text, cancellationToken).ConfigureAwait(false);
        if (native is not null)
        {
            return new(
                native.RequiresDeepProcessing
                    ? DesktopQuickAnswerDisposition.RequiresDeepProcessing
                    : DesktopQuickAnswerDisposition.Answered,
                native.Title,
                native.Message);
        }

        if (model is null || !model.IsAvailable || model.Settings is null)
            return new(
                DesktopQuickAnswerDisposition.RequiresModel,
                "CLI · 需要 AI 模型",
                "当前请求不是已支持的本地 CLI 指令，请先配置一个可用模型。/status、/time、/apps、/files、/open 和 /open-file 等本地能力仍然可用。");

        var settings = model.Settings;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
            return new(
                DesktopQuickAnswerDisposition.RequiresModel,
                "模型配置不完整",
                "请检查 Base URL 和模型 ID。本地 CLI 能力不受影响，模型错误也不会自动启动 Harness。");

        var modelKey = $"{settings.ProviderId}|{settings.BaseUrl.Trim()}|{settings.Model.Trim()}";
        EnsureConversationModel(modelKey);
        var intent = Classify(text);
        var history = SnapshotHistoryForContext();

        try
        {
            void PublishPartial(string partial)
            {
                onPartial?.Invoke(partial);
                PartialResponseUpdated?.Invoke(partial);
            }

            var reply = await _provider.CompleteAsync(
                settings,
                model.Credential,
                BuildSystemPrompt(intent),
                history,
                text,
                intent == QuickIntent.Translate ? 512 : 1400,
                PublishPartial,
                cancellationToken).ConfigureAwait(false);

            if (TryExtractHarnessEscalation(reply, out var reason))
                return new(
                    DesktopQuickAnswerDisposition.RequiresDeepProcessing,
                    "CLI · 超出本地能力边界",
                    reason);

            AppendTurn(text, reply);
            return new(
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
            return new(
                DesktopQuickAnswerDisposition.Answered,
                "CLI 连接失败",
                $"{error.Message} 请检查当前模型配置或稍后重试。本地 CLI 工具仍可继续使用，不会自动启动 Harness。");
        }
    }

    public void ResetConversation()
    {
        lock (_historyGate)
        {
            _history.Clear();
            _conversationModelKey = null;
            _sessionStore.Clear();
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
You are the language layer inside TuringDesk Level-3 CLI / light Agent.
{task}
TuringDesk has a separate bounded native tool controller for safe local operations such as status inspection, app/file search and explicit app/file opening. If this model call is reached, those deterministic tools did not handle the request.
You do NOT have raw PowerShell, CMD, filesystem, process-control, administrator or arbitrary Windows access inside this model call, so never claim that you changed the computer.
Do not escalate ordinary questions, explanations, writing, translation, coding advice, calculations, or model/network errors.
Do not request L4 merely because the user mentions a local app or file. For a simple supported local action, tell the user they can use /help or the corresponding L3 command if appropriate.
Only when the requested outcome genuinely needs capabilities outside bounded L3 — for example arbitrary shell execution, destructive/privileged changes, workspace/skills/jobs, broad local inspection, or complex multi-step autonomous tool execution — respond with this marker as the FIRST line:
{HarnessMarker}
Then briefly explain why Level 4 Harness is required.
Otherwise answer normally and never mention Harness.
""";
    }

    private void EnsureConversationModel(string modelKey)
    {
        lock (_historyGate)
        {
            if (string.Equals(_conversationModelKey, modelKey, StringComparison.Ordinal)) return;
            _conversationModelKey = modelKey;
            _history.Clear();
            _history.AddRange(_sessionStore.Load(modelKey));
            TrimHistoryNoLock();
        }
    }

    private IReadOnlyList<L3ChatMessage> SnapshotHistoryForContext()
    {
        lock (_historyGate)
        {
            var selected = new List<L3ChatMessage>();
            var characters = 0;
            for (var index = _history.Count - 1; index >= 0; index--)
            {
                var message = _history[index];
                var length = message.Content?.Length ?? 0;
                if (selected.Count >= MaxConversationMessages || characters + length > MaxContextCharacters)
                    break;
                selected.Add(message);
                characters += length;
            }
            selected.Reverse();
            return selected;
        }
    }

    private void AppendTurn(string userText, string assistantText)
    {
        lock (_historyGate)
        {
            _history.Add(new L3ChatMessage("user", userText));
            _history.Add(new L3ChatMessage("assistant", assistantText));
            TrimHistoryNoLock();
            if (_conversationModelKey is not null)
                _sessionStore.Save(_conversationModelKey, _history);
        }
    }

    private void TrimHistoryNoLock()
    {
        while (_history.Count > MaxConversationMessages)
            _history.RemoveAt(0);

        var total = _history.Sum(item => item.Content?.Length ?? 0);
        while (_history.Count > 2 && total > MaxContextCharacters)
        {
            total -= _history[0].Content?.Length ?? 0;
            _history.RemoveAt(0);
        }
    }

    private static bool TryExtractHarnessEscalation(string reply, out string reason)
    {
        reason = string.Empty;
        if (!reply.StartsWith(HarnessMarker, StringComparison.Ordinal)) return false;
        reason = reply[HarnessMarker.Length..].TrimStart('\r', '\n', ' ', ':', '：').Trim();
        if (string.IsNullOrWhiteSpace(reason))
            reason = "这个请求需要超出 L3 白名单的本机能力、工作区能力或复杂多步骤 Agent 工作流。";
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
            return double.Parse(_text[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture);
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
