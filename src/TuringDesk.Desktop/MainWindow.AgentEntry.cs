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
                const string offline = "AI Runtime 离线或当前模型无法响应。请从搜索框右侧的设置进入 AI 配置。";
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
