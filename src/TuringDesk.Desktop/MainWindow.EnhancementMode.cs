using System.Windows;
using System.Windows.Threading;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class MainWindow
{
    private EnhancementWallpaperWindow? _enhancementWallpaper;

    /// <summary>
    /// Default TuringDesk mode: keep Explorer as the Windows shell and attach
    /// only TuringDesk's visual scene behind Explorer desktop icons. Agent,
    /// Harness, voice and floating-card services remain owned by TuringDesk.
    /// </summary>
    internal void EnableEnhancementMode()
    {
        if (ShellSession.IsShellMode) return;

        ShellSession.IsEnhancementMode = true;
        Title = "TuringDesk · Desktop Enhancement";
        Loaded += EnhancementMode_Loaded;
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

            if (wallpaper.IsAttached)
            {
                AddActivity("desktop", "Desktop Enhancement Mode attached to Explorer wallpaper layer. Explorer remains the shell.");
                ShellNotificationService.Publish(
                    "桌面增强模式已启用",
                    "保留 Windows Explorer、任务栏和桌面图标；TuringDesk 只接管桌面视觉与 AI 增强层。",
                    "shell");
            }
            else
            {
                AddActivity("desktop", "Explorer wallpaper host was unavailable. TuringDesk left the Windows desktop untouched.");
                ShellNotificationService.Publish(
                    "桌面视觉层暂未挂载",
                    "Explorer 仍正常工作；TuringDesk AI、Harness 与语音功能不受影响。",
                    "warning");
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private void EnhancementMode_Closed(object? sender, EventArgs e)
    {
        ShellSession.IsEnhancementMode = false;
        if (_enhancementWallpaper is null) return;

        try { _enhancementWallpaper.Close(); } catch { }
        _enhancementWallpaper = null;
    }
}
