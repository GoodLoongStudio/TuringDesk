using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Explicit Agent entry point. RuntimeClient owns the lazy Runtime lease, so
    /// callers do not need to pre-warm Node/Harness themselves.
    /// </summary>
    internal async Task<string> SubmitSearchCommandAsync(string text, CancellationToken cancellationToken = default)
    {
        var prompt = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;

        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(() => SubmitSearchCommandAsync(prompt, cancellationToken)).Task.Unwrap();
        }

        AddActivity("you", prompt);
        SetAgentState("正在理解你的请求…", TrimForUi(prompt, 120), Color.FromRgb(135, 150, 255));
        AgentFloatingCardsService.Begin(DisplayManager.GetPrimary(), prompt);

        try
        {
            var reply = await _runtime.ChatAsync(prompt, cancellationToken);
            if (reply is null)
            {
                const string offline = "AI Runtime 离线或当前模型无法响应。请检查模型配置后重试。";
                AddActivity("ai", offline);
                SetAgentState("这次没有完成", offline, Color.FromRgb(240, 125, 125));
                AgentFloatingCardsService.Fail(offline);
                return offline;
            }

            AddActivity("ai", reply);
            SetAgentState("已完成", TrimForUi(reply, 150), Color.FromRgb(84, 214, 138));
            AgentFloatingCardsService.Complete(reply);
            return reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AddActivity("ai", "Agent request was cancelled.");
            SetAgentState("等待下一条指令", "上一次请求已取消。", Color.FromRgb(135, 150, 255));
            AgentFloatingCardsService.Hide();
            throw;
        }
    }

    /// <summary>
    /// Full Agent path used by explicit deep surfaces. RuntimeClient.ConfigureModelAsync
    /// now acquires the lazy Runtime lease itself; do not pre-start Runtime here.
    /// </summary>
    internal async Task<string> SubmitSearchCommandWithModelAsync(
        string text,
        DesktopAiModelChoice choice,
        CancellationToken cancellationToken = default)
    {
        if (!choice.IsAvailable || choice.Settings is null)
            return "还没有可用的 AI 模型。点击搜索框左侧的模型选择器，粘贴 API Key 或配置本地模型后即可使用。";

        try
        {
            var configured = await _runtime.ConfigureModelAsync(choice.Settings, choice.Credential, cancellationToken);
            if (configured is null)
            {
                var failed = $"{choice.DisplayName} 暂时不可用。请检查 API Key、模型 ID 或本地模型服务。";
                AddActivity("model", failed);
                SetAgentState("模型连接失败", failed, Color.FromRgb(240, 125, 125));
                return failed;
            }

            _modelSettings = choice.Settings;
            UpdateModelStatus();
            return await SubmitSearchCommandAsync(text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var failed = $"{choice.DisplayName} 配置失败：{error.Message}";
            AddActivity("model", failed);
            SetAgentState("模型连接失败", TrimForUi(failed, 150), Color.FromRgb(240, 125, 125));
            return failed;
        }
    }

    internal IReadOnlyList<DesktopAiModelChoice> LoadDesktopSearchModelChoices() =>
        new DesktopAiModelChoiceService(_modelStore).LoadChoices();

    internal DesktopAiModelChoice? ResolveDesktopSearchDefaultModel(IReadOnlyList<DesktopAiModelChoice> choices) =>
        new DesktopAiModelChoiceService(_modelStore).ResolveDefault(choices);

    internal void ShowAiModelSettingsFromSearch()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowAiModelSettingsFromSearch);
            return;
        }

        var current = _modelStore.Load();
        var apiKey = _modelStore.LoadApiKey();
        var window = new ModelSettingsWindow(_runtime, _modelStore, current, apiKey);
        window.Closed += (_, _) =>
        {
            _modelSettings = window.SavedSettings ?? _modelStore.Load();
            UpdateModelStatus();
        };
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// The search bar calls this only after the user explicitly presses
    /// "深度处理" (or Ctrl+Enter). The query is carried into Harness and this is
    /// the deliberate boundary where the heavy Agent stack may be started.
    /// </summary>
    internal void ShowHarnessConsoleFromSearch(string query)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowHarnessConsoleFromSearch(query));
            return;
        }

        var prompt = query.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        var window = new HarnessConsoleWindow(prompt);
        window.Show();
        window.Activate();
    }

    internal async Task SubmitExternalCommandAsync(string text)
    {
        _ = await SubmitSearchCommandAsync(text);
    }

    internal void RequestApplicationExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RequestApplicationExit);
            return;
        }

        ShellSession.ExitRequested = true;
        Close();
    }
}
