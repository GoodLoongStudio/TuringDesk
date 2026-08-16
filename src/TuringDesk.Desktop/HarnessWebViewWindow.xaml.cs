using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class HarnessWebViewWindow : Window
{
    private readonly RuntimeClient _runtime;
    private Uri? _harnessUri;
    private bool _loading;

    public HarnessWebViewWindow(RuntimeClient runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Loaded += async (_, _) => await LoadHarnessAsync();
        StateChanged += (_, _) => UpdateMaximizeIcon();
    }

    private async Task LoadHarnessAsync()
    {
        if (_loading) return;
        _loading = true;
        RetryButton.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        HarnessWebView.Visibility = Visibility.Hidden;
        StatusText.Text = "正在启动官方 WebUI…";
        LoadingText.Text = "TuringDesk 正在加载 DeepSeek Harness 官方界面，不会打开浏览器窗口。";

        try
        {
            var state = await _runtime.EnsureHarnessWebAsync();
            if (state is null || !state.Ok || !Uri.TryCreate(state.Url, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException(state?.Error ?? "TuringDesk Runtime 无法启动 DeepSeek Harness WebUI。") ;
            }

            _harnessUri = uri;
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TuringDesk",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await HarnessWebView.EnsureCoreWebView2Async(environment);

            var core = HarnessWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.NewWindowRequested += Core_NewWindowRequested;

            StatusText.Text = "官方 WebUI · 本机运行";
            HarnessWebView.Source = uri;
        }
        catch (Exception error)
        {
            StatusText.Text = "Harness WebUI 启动失败";
            LoadingText.Text = error.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
        }
    }

    private void HarnessWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingText.Text = $"DeepSeek Harness 页面加载失败：{e.WebErrorStatus}";
            RetryButton.Visibility = Visibility.Visible;
            StatusText.Text = "页面加载失败";
            return;
        }

        HarnessWebView.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "官方 WebUI · WebView2";
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)) return;

        if (_harnessUri is not null
            && string.Equals(target.Host, _harnessUri.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == _harnessUri.Port)
        {
            HarnessWebView.Source = target;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // External links are optional; keep the embedded Harness session intact.
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadHarnessAsync();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (HarnessWebView.CoreWebView2 is not null)
        {
            HarnessWebView.Reload();
        }
        else
        {
            _ = LoadHarnessAsync();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeIcon();
    }

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null) return;
        MaximizeIcon.Kind = WindowState == WindowState.Maximized ? "RestoreWindow" : "Maximize";
    }
}
