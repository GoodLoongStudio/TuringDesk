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
        LoadingTitle.Text = "正在启动 Agent 控制台";
        LoadingDetail.Text = "按需启动本机 DeepSeek Harness WebUI…";
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
            HarnessView.Source = url;
        }
        catch (OperationCanceledException)
        {
            // Window closed or a retry superseded this load.
        }
        catch (Exception error)
        {
            LoadingTitle.Text = "Agent 控制台启动失败";
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
            LoadingTitle.Text = "Agent 控制台无法加载";
            LoadingDetail.Text = $"Harness WebUI 返回了导航错误：{e.WebErrorStatus}";
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
            _ = await HarnessView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            // WebUI DOM can change between Harness versions. The workbench is
            // still open and usable even if best-effort query prefill misses.
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
        if (HarnessView.CoreWebView2 is not null)
        {
            HarnessView.CoreWebView2.Reload();
            return;
        }

        _ = LoadHarnessAsync();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadHarnessAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount > 1) return;
        try { DragMove(); } catch { }
    }
}
