using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private EnhancementWallpaperWindow? _enhancementWallpaper;
    private AgentOrbWindow? _agentOrb;

    /// <summary>
    /// Default TuringDesk mode: keep Explorer as the Windows shell and attach
    /// the desktop engine behind Explorer icons. The control center is secondary;
    /// normal users enter through the Orb, Alt+Space or voice.
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
        if (!ShellSession.IsEnhancementMode || _enhancementWallpaper is not null) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ShellSession.IsEnhancementMode || _enhancementWallpaper is not null) return;

            var wallpaper = new EnhancementWallpaperWindow();
            _enhancementWallpaper = wallpaper;
            wallpaper.Show();

            _agentOrb = new AgentOrbWindow(this);
            _agentOrb.Show();

            if (wallpaper.IsAttached)
            {
                AddActivity("desktop", "Desktop Engine attached behind Explorer icons; Explorer remains the Windows shell.");
                ShellNotificationService.Publish(
                    "TuringDesk AI Desktop 已就绪",
                    "按 Alt+Space、点击右下角 Orb 或直接说“图灵桌面”。",
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

            // Product default: do not greet users with an engineering console.
            Hide();
        }), DispatcherPriority.ApplicationIdle);
    }

    private void EnhancementMode_Closing(object? sender, CancelEventArgs e)
    {
        if (!ShellSession.IsEnhancementMode || ShellSession.ExitRequested) return;

        // Closing the control center should not kill the desktop engine.
        e.Cancel = true;
        Hide();
    }

    private void EnhancementMode_Closed(object? sender, EventArgs e)
    {
        ShellSession.IsEnhancementMode = false;

        if (_agentOrb is not null)
        {
            try { _agentOrb.Close(); } catch { }
            _agentOrb = null;
        }

        if (_enhancementWallpaper is not null)
        {
            try { _enhancementWallpaper.Close(); } catch { }
            _enhancementWallpaper = null;
        }
    }
}
