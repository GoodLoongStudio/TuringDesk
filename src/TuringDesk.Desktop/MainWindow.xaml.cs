using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow : Window
{
    private readonly RuntimeClient _runtime = new();
    private readonly AppLauncher _launcher = new();
    private readonly WindowManager _windows = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var health = await _runtime.GetHealthAsync();
        if (health is not null)
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(74, 222, 128));
            RuntimeStatus.Text = $"Runtime {health.Mode}";
            AddActivity("system", $"AI runtime connected ({health.Mode}).");
        }
        else
        {
            RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            RuntimeStatus.Text = "Runtime offline";
            AddActivity("system", "Runtime is offline. Native desktop commands still work.");
        }
    }

    private async void Chrome_Click(object sender, RoutedEventArgs e) => await LaunchAsync("chrome");
    private async void VSCode_Click(object sender, RoutedEventArgs e) => await LaunchAsync("code");
    private async void Terminal_Click(object sender, RoutedEventArgs e) => await LaunchAsync("terminal");

    private async Task LaunchAsync(string app)
    {
        var launched = await _launcher.LaunchAsync(app);
        AddActivity("app", launched ? $"Launched {app}." : $"Could not launch {app}.");
    }

    private async void Ask_Click(object sender, RoutedEventArgs e) => await SubmitCommandAsync();

    private async void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitCommandAsync();
        }
    }

    private async Task SubmitCommandAsync()
    {
        var text = CommandBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        CommandBox.Clear();
        AddActivity("you", text);

        if (await TryNativeDemoCommandAsync(text)) return;

        var reply = await _runtime.ChatAsync(text);
        AddActivity("ai", reply ?? "Runtime is offline. Start `pnpm dev` in the runtime folder.");
    }

    private async Task<bool> TryNativeDemoCommandAsync(string text)
    {
        var command = text.ToLowerInvariant();
        var asksChrome = command.Contains("chrome") || command.Contains("浏览器");
        var asksCode = command.Contains("vs code") || command.Contains("vscode") || command.Contains("code");
        var asksTile = command.Contains("左右") || command.Contains("并排") || command.Contains("side by side") || command.Contains("tile");

        if (!asksChrome || !asksCode || !asksTile) return false;

        AddActivity("agent", "Launching Chrome and VS Code…");
        await _launcher.LaunchAsync("chrome");
        await _launcher.LaunchAsync("code");

        var chrome = await _windows.WaitForWindowAsync(new[] { "chrome", "google chrome" }, TimeSpan.FromSeconds(8));
        var code = await _windows.WaitForWindowAsync(new[] { "visual studio code", "code" }, TimeSpan.FromSeconds(8));

        if (chrome == IntPtr.Zero || code == IntPtr.Zero)
        {
            AddActivity("agent", "Apps were launched, but I could not find both top-level windows yet.");
            return true;
        }

        _windows.TileSideBySide(chrome, code);
        AddActivity("agent", "Done — Chrome is on the left and VS Code is on the right.");
        return true;
    }

    private void AddActivity(string source, string message)
    {
        ActivityList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {source}: {message}");
    }
}
