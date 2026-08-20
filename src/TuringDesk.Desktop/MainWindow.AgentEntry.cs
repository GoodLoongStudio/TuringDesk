using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
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
        var window = new ModelSettingsWindow(_modelStore, current, apiKey);
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// The search bar calls this only after the user explicitly presses
    /// "深度处理" (or Ctrl+Enter). The query is carried into the official
    /// DeepSeek Harness WebUI and this is the deliberate boundary where the
    /// workbench may be started.
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
