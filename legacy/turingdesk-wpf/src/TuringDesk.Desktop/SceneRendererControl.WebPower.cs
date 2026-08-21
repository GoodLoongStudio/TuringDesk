using System.Windows;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private bool _webPowerLifecycleHooked;
    private int _webSuspendTransition;

    private void EnsureWebPowerLifecycle()
    {
        if (_webPowerLifecycleHooked) return;
        _webPowerLifecycleHooked = true;
        WebPlayer.IsVisibleChanged += WebPlayer_IsVisibleChangedForPower;
    }

    private async void WebPlayer_IsVisibleChangedForPower(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_scene?.Kind != SceneKind.Web || WebPlayer.CoreWebView2 is null) return;
        if (Interlocked.Exchange(ref _webSuspendTransition, 1) != 0) return;

        try
        {
            if (!WebPlayer.IsVisible || _paused || _stopped || !IsLoaded)
            {
                // CoreWebView2 requires the controller to be invisible before
                // TrySuspendAsync. Pause() collapses WebPlayer first, and Unloaded
                // also reaches this branch. Suspension stops page timers/animations
                // in addition to TuringDesk's own input/audio loops.
                if (!WebPlayer.CoreWebView2.IsSuspended)
                {
                    try { _ = await WebPlayer.CoreWebView2.TrySuspendAsync(); }
                    catch { }
                }
                return;
            }

            if (WebPlayer.CoreWebView2.IsSuspended)
            {
                try { WebPlayer.CoreWebView2.Resume(); }
                catch { }
            }
        }
        finally
        {
            Volatile.Write(ref _webSuspendTransition, 0);
        }
    }

    private void ResumeWebRendererIfNeeded()
    {
        if (WebPlayer.CoreWebView2 is null) return;
        try
        {
            if (WebPlayer.CoreWebView2.IsSuspended)
                WebPlayer.CoreWebView2.Resume();
        }
        catch { }
    }
}
