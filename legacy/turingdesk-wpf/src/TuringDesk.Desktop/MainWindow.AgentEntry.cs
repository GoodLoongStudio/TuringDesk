using System.Windows;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private ModelSettingsWindow? _modelSettingsWindow;

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

        if (_modelSettingsWindow is { IsVisible: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.Focus();
            return;
        }

        var current = _modelStore.Load();
        var apiKey = _modelStore.LoadApiKey();
        var window = new ModelSettingsWindow(_modelStore, current, apiKey);
        _modelSettingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_modelSettingsWindow, window))
                _modelSettingsWindow = null;
        };
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

    internal void ShowHarnessConsoleFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowHarnessConsoleFromTray);
            return;
        }

        var existing = Application.Current.Windows
            .OfType<HarnessConsoleWindow>()
            .FirstOrDefault(window => window.IsVisible);
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.Focus();
            return;
        }

        var window = new HarnessConsoleWindow();
        window.Show();
        window.Activate();
        DesktopDiagnostics.Info("tray.action", "open-harness");
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
