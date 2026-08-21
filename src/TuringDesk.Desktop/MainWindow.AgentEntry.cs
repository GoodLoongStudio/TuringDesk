using System.Windows;
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

    internal void ShowDesktopSearchFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowDesktopSearchFromTray);
            return;
        }

        if (_desktopSearchBar is not null)
        {
            _desktopSearchBar.FocusSearch();
            return;
        }

        ShellNotificationService.Publish(
            "图灵搜索尚未就绪",
            "桌面搜索入口仍在初始化，请稍后再试。",
            "warning");
    }

    internal void RequestApplicationExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RequestApplicationExit);
            return;
        }

        DesktopDiagnostics.Info("shutdown.request", "source=tray-or-ui");
        ShellSession.ExitRequested = true;
        Application.Current.Shutdown(0);
    }
}
