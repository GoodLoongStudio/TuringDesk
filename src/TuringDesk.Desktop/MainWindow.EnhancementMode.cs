using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private readonly List<EnhancementWallpaperWindow> _enhancementWallpapers = [];
    private DesktopSearchBarWindow? _desktopSearchBar;
    private DispatcherTimer? _enhancementDisplayTimer;
    private string _enhancementDisplaySignature = string.Empty;

    /// <summary>
    /// Default TuringDesk mode: Explorer stays the Windows shell. Each physical
    /// monitor gets its own wallpaper-engine child window while one top-center
    /// search bar becomes the primary AI/desktop entry on the primary monitor.
    /// MainWindow is an invisible runtime owner only; the legacy dashboard is not
    /// presented to users in enhancement mode.
    /// </summary>
    internal void EnableEnhancementMode()
    {
        if (ShellSession.IsShellMode) return;

        ShellSession.IsEnhancementMode = true;
        ShellSession.ExitRequested = false;
        Title = "TuringDesk · Desktop Runtime Host";

        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Opacity = 0;

        Loaded += EnhancementMode_Loaded;
        Closing += EnhancementMode_Closing;
        Closed += EnhancementMode_Closed;
    }

    private void EnhancementMode_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ShellSession.IsEnhancementMode || _enhancementWallpapers.Count > 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ShellSession.IsEnhancementMode || _enhancementWallpapers.Count > 0) return;

            RebuildEnhancementMonitors(force: true);
            _enhancementDisplayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _enhancementDisplayTimer.Tick += (_, _) =>
            {
                RebuildEnhancementMonitors(force: false);
                _desktopSearchBar?.RefreshPosition();
            };
            _enhancementDisplayTimer.Start();

            var attached = _enhancementWallpapers.Count(window => window.IsAttached);
            if (attached > 0)
            {
                AddActivity("desktop", $"Desktop Engine attached on {attached}/{_enhancementWallpapers.Count} monitor(s); Explorer remains the Windows shell.");
            }
            else
            {
                AddActivity("desktop", "Explorer wallpaper host was unavailable. Search/AI remains available while the Windows desktop stays untouched.");
                ShellNotificationService.Publish(
                    "桌面引擎正在等待 Explorer",
                    "Windows 桌面不受影响；顶部 AI 搜索和语音仍可正常使用。",
                    "warning");
            }

            var onboarding = new OnboardingStateStore();
            if (!onboarding.IsCompleted())
            {
                var setup = new FirstRunSetupWindow(_runtime, _modelStore);
                setup.ShowDialog();
            }

            _desktopSearchBar = new DesktopSearchBarWindow(this);
            _desktopSearchBar.Show();
            _desktopSearchBar.RefreshPosition();

            ShellNotificationService.Publish(
                "TuringDesk AI Desktop 已就绪",
                "直接使用屏幕上方搜索框，或按 Alt+Space 聚焦。右侧设计按钮进入桌面库与综合设置。",
                "shell");

            Hide();
        }), DispatcherPriority.ApplicationIdle);
    }

    private void RebuildEnhancementMonitors(bool force)
    {
        var signature = DisplayManager.GetSignature();
        if (!force && signature == _enhancementDisplaySignature && _enhancementWallpapers.Count > 0) return;
        _enhancementDisplaySignature = signature;

        foreach (var wallpaper in _enhancementWallpapers.ToArray())
        {
            try { wallpaper.Close(); } catch { }
        }
        _enhancementWallpapers.Clear();

        foreach (var monitor in DisplayManager.GetMonitors())
        {
            var wallpaper = new EnhancementWallpaperWindow(monitor);
            _enhancementWallpapers.Add(wallpaper);
            wallpaper.Show();
        }

        _desktopSearchBar?.RefreshPosition();
    }

    private void EnhancementMode_Closing(object? sender, CancelEventArgs e)
    {
        if (!ShellSession.IsEnhancementMode || ShellSession.ExitRequested) return;
        e.Cancel = true;
        Hide();
    }

    private void EnhancementMode_Closed(object? sender, EventArgs e)
    {
        ShellSession.IsEnhancementMode = false;
        _enhancementDisplayTimer?.Stop();
        _enhancementDisplayTimer = null;

        if (_desktopSearchBar is not null)
        {
            try { _desktopSearchBar.Close(); } catch { }
            _desktopSearchBar = null;
        }

        foreach (var wallpaper in _enhancementWallpapers.ToArray())
        {
            try { wallpaper.Close(); } catch { }
        }
        _enhancementWallpapers.Clear();
    }
}
