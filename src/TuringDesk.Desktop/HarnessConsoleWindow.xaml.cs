using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class HarnessConsoleWindow : Window
{
    private readonly string? _initialQuery;
    private CancellationTokenSource? _loadCancellation;
    private bool _lifecycleOpened;
    private bool _prefillAttempted;

    public HarnessConsoleWindow(string? initialQuery = null)
    {
        _initialQuery = string.IsNullOrWhiteSpace(initialQuery) ? null : initialQuery.Trim();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            OpenLifecycle();
            await LoadHarnessAsync();
        };
        Closed += (_, _) => CloseLifecycle();
    }

    private void OpenLifecycle()
    {
        if (_lifecycleOpened) return;
        _lifecycleOpened = true;
        HarnessWebUiService.NotifyConsoleOpened();
    }

    private void CloseLifecycle()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        if (!_lifecycleOpened) return;
        _lifecycleOpened = false;
        HarnessWebUiService.NotifyConsoleClosed();
    }

    private async Task LoadHarnessAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        HarnessView.Visibility = Visibility.Collapsed;
        LoadingTitle.Text = "正在启动 AI 工作台";
        LoadingDetail.Text = "正在启动官方 DeepSeek Harness 工作台…";
        LoadingProgress.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;

        try
        {
            var url = await HarnessWebUiService.EnsureRunningAsync(cancellationToken);

            var webViewProfile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TuringDesk",
                "WebView2");
            Directory.CreateDirectory(webViewProfile);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewProfile);
            await HarnessView.EnsureCoreWebView2Async(environment);

            ConfigureBrowserSurface();

            // Spec §8.4: prefer official URL/session interface over DOM injection.
            // Append the initial query as a URL hash so the WebUI can pick it up
            // if it supports hash-based prefill. DOM injection remains as fallback.
            var targetUrl = _initialQuery is not null
                ? new UriBuilder(url) { Fragment = $"prompt={Uri.EscapeDataString(_initialQuery)}" }.Uri
                : url;
            HarnessView.Source = targetUrl;
        }
        catch (OperationCanceledException)
        {
            // Window closed or a retry superseded this load.
        }
        catch (Exception error)
        {
            LoadingTitle.Text = "AI 工作台启动失败";
            LoadingDetail.Text = error.Message;
            LoadingProgress.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void ConfigureBrowserSurface()
    {
        if (HarnessView.CoreWebView2 is not { } core) return;

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        try
        {
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        }
        catch
        {
            // Older WebView2 runtimes may not expose the preference at runtime.
        }

        core.NavigationStarting -= Core_NavigationStarting;
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted -= Core_NavigationCompleted;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested -= Core_NewWindowRequested;
        core.NewWindowRequested += Core_NewWindowRequested;

        // Spec §8.5: downloads, popups and permission requests need explicit policies.
        // Downloads are blocked in the workbench shell — the Harness WebUI manages
        // its own file operations within the sandboxed profile.
        core.DownloadStarting -= Core_DownloadStarting;
        core.DownloadStarting += Core_DownloadStarting;

        // Permission requests (camera, mic, geolocation, etc.) are denied by
        // default. The Harness WebUI should not need device access through the
        // shell. If a future feature requires it, add an explicit user prompt.
        core.PermissionRequested -= Core_PermissionRequested;
        core.PermissionRequested += Core_PermissionRequested;
    }

    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // Block all downloads from the workbench shell.
        e.Cancel = true;
    }

    private void Core_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        if (IsHarnessLoopback(uri)) return;

        e.Cancel = true;
        OpenExternal(uri);
    }

    private async void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            HarnessView.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingTitle.Text = "AI 工作台无法加载";
            LoadingDetail.Text = $"DeepSeek Harness WebUI 返回了导航错误：{e.WebErrorStatus}";
            LoadingProgress.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
            return;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        HarnessView.Visibility = Visibility.Visible;
        await TryPrefillInitialQueryAsync();
    }

    private async Task TryPrefillInitialQueryAsync()
    {
        if (_prefillAttempted || string.IsNullOrWhiteSpace(_initialQuery) || HarnessView.CoreWebView2 is null)
            return;

        _prefillAttempted = true;
        var encoded = JsonSerializer.Serialize(_initialQuery);
        var script = $$"""
(() => {
  const query = {{encoded}};
  const element = document.querySelector('textarea, input[type="text"], [contenteditable="true"]');
  if (!element) return false;
  element.focus();
  if (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement) {
    const proto = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(element, query); else element.value = query;
  } else {
    element.textContent = query;
  }
  element.dispatchEvent(new Event('input', { bubbles: true }));
  element.dispatchEvent(new Event('change', { bubbles: true }));
  return true;
})()
""";

        try
        {
            var result = await HarnessView.CoreWebView2.ExecuteScriptAsync(script);
            if (!string.Equals(result?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                PreserveInitialQueryForManualHandoff();
        }
        catch
        {
            // WebUI DOM can change between Harness versions. Never silently lose
            // the original L3 request: keep it available for manual paste instead.
            PreserveInitialQueryForManualHandoff();
        }
    }

    private void PreserveInitialQueryForManualHandoff()
    {
        if (string.IsNullOrWhiteSpace(_initialQuery)) return;

        try
        {
            Clipboard.SetText(_initialQuery);
            ShellNotificationService.Publish(
                "深度任务已保留",
                "AI 工作台未识别自动预填入口；原始问题已复制到剪贴板，可直接粘贴后继续。",
                "warning");
        }
        catch
        {
            ShellNotificationService.Publish(
                "深度任务未自动填入",
                $"请返回搜索栏复制原始问题后重试：{_initialQuery}",
                "warning");
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;

        if (IsHarnessLoopback(uri))
        {
            HarnessView.Source = uri;
            return;
        }

        OpenExternal(uri);
    }

    private static bool IsHarnessLoopback(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        uri.Port == HarnessWebUiService.Url.Port &&
        (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // External documentation links are non-essential to the local console.
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _prefillAttempted = false;
        if (HarnessView.CoreWebView2 is not null)
        {
            HarnessView.CoreWebView2.Reload();
            return;
        }

        _ = LoadHarnessAsync();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        _prefillAttempted = false;
        await LoadHarnessAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount > 1) return;
        try { DragMove(); } catch { }
    }
}
