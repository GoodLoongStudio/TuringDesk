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
    /// TuringDesk's visual scene behind Explorer desktop icons. The control
    /// center becomes secondary; the desktop Orb is the primary quick UI.
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
                AddActivity("desktop", "Animated Scene attached behind Explorer desktop icons; Explorer remains the shell.");
                ShellNotificationService.Publish(
                    "TuringDesk AI Desktop 已就绪",
                    "动态 Scene 已挂到 Windows 桌面；按 Alt+Space 或点击右下角 Orb 直接唤起 AI。",
                    "shell");
            }
            else
            {
                AddActivity("desktop", "Explorer wallpaper host was unavailable. Agent Orb remains available while Windows desktop stays untouched.");
                ShellNotificationService.Publish(
                    "Scene 暂未挂载",
                    "Explorer 仍正常工作；AI Orb、Harness 与语音功能不受影响。",
                    "warning");
            }

            // Product default: do not greet the user with an engineering console.
            // Services are already bootstrapping from MainWindow.Loaded; hide the
            // control center and let the desktop scene + Orb be the first surface.
            Hide();
        }), DispatcherPriority.ApplicationIdle);
    }

    private void EnhancementMode_Closing(object? sender, CancelEventArgs e)
    {
        if (!ShellSession.IsEnhancementMode || ShellSession.ExitRequested) return;

        // Closing the control center should not kill the desktop engine. It is a
        // secondary settings/status surface in Enhancement Mode.
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
