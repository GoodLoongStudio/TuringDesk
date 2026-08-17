using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private readonly List<EnhancementWallpaperWindow> _enhancementWallpapers = [];
    private AgentOrbWindow? _agentOrb;
    private DispatcherTimer? _enhancementDisplayTimer;
    private string _enhancementDisplaySignature = string.Empty;

    /// <summary>
    /// Default TuringDesk mode: Explorer stays the Windows shell. Each physical
    /// monitor gets its own wallpaper-engine child window so profiles can assign
    /// independent scenes/playlists without replacing Explorer.
    /// </summary>
    internal void EnableEnhancementMode()
    {
        if (ShellSession.IsShellMode) return;

        ShellSession.IsEnhancementMode = true;
        ShellSession.ExitRequested = false;
        Title = "TuringDesk · AI Desktop";
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
            _enhancementDisplayTimer.Tick += (_, _) => RebuildEnhancementMonitors(force: false);
            _enhancementDisplayTimer.Start();

            _agentOrb = new AgentOrbWindow(this);
            _agentOrb.Show();

            var attached = _enhancementWallpapers.Count(window => window.IsAttached);
            if (attached > 0)
            {
                AddActivity("desktop", $"Desktop Engine attached on {attached}/{_enhancementWallpapers.Count} monitor(s); Explorer remains the Windows shell.");
                ShellNotificationService.Publish(
                    "TuringDesk AI Desktop 已就绪",
                    _enhancementWallpapers.Count > 1
                        ? $"{_enhancementWallpapers.Count} 块显示器已启用独立桌面引擎。按 Alt+Space 直接使用 AI。"
                        : "按 Alt+Space、点击右下角 Orb 或直接说“图灵桌面”。",
                    "shell");
            }
            else
            {
                AddActivity("desktop", "Explorer wallpaper host was unavailable. Agent entry remains available while the Windows desktop stays untouched.");
                ShellNotificationService.Publish(
                    "桌面引擎正在等待 Explorer",
                    "Windows 桌面不受影响；AI 与语音仍可正常使用。",
                    "warning");
            }

            var onboarding = new OnboardingStateStore();
            if (!onboarding.IsCompleted())
            {
                var setup = new FirstRunSetupWindow(_runtime, _modelStore) { Owner = this };
                setup.ShowDialog();
            }

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

        if (_agentOrb is not null)
        {
            try { _agentOrb.Close(); } catch { }
            _agentOrb = null;
        }

        foreach (var wallpaper in _enhancementWallpapers.ToArray())
        {
            try { wallpaper.Close(); } catch { }
        }
        _enhancementWallpapers.Clear();
    }
}
