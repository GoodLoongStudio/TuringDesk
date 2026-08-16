using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class HarnessConsoleWindow : Window
{
    private CancellationTokenSource? _loadCancellation;

    public HarnessConsoleWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadHarnessAsync();
        Closed += (_, _) =>
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        };
    }

    private async Task LoadHarnessAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();

        LoadingPanel.Visibility = Visibility.Visible;
        HarnessView.Visibility = Visibility.Collapsed;
        LoadingTitle.Text = "正在启动 Agent 控制台";
        LoadingDetail.Text = "正在启动本机 DeepSeek Harness WebUI…";
        LoadingProgress.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;

        try
        {
            var url = await HarnessWebUiService.EnsureRunningAsync(_loadCancellation.Token);
            await HarnessView.EnsureCoreWebView2Async();
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

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
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
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { }
    }
}
