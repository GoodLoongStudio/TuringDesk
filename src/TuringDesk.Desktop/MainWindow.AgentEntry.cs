using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Entry point used by the desktop-native top search bar. It reuses the
    /// existing Runtime/Harness-backed chat path and floating trace cards while
    /// returning the reply so the search bar can remain the primary conversation UI.
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
                const string offline = "AI Runtime 离线或当前模型无法响应。请从搜索框左侧选择模型，或进入 AI 配置。";
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
            AddActivity("ai", "Desktop search request timed out after 30 seconds and was cancelled.");
            SetAgentState("等待下一条指令", "上一次请求超过 30 秒未响应，搜索框已恢复。", Color.FromRgb(135, 150, 255));
            AgentFloatingCardsService.Hide();
            throw;
        }
    }

    /// <summary>
    /// The search bar calls this overload so its visible model selection is the
    /// model that actually answers the prompt. This avoids the old failure mode
    /// where the UI displayed one model while Runtime still used another one.
    /// The selection is transient unless the user explicitly saves it in model
    /// settings; the persisted beginner/Harness configuration remains the source
    /// of truth for the next launch.
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
            await RuntimeHostService.EnsureRunningAsync();
            var configured = await _runtime.ConfigureModelAsync(choice.Settings, choice.Credential);
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
        ShowDiyCenter();
        _modelSettings = _modelStore.Load();
        UpdateModelStatus();
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
