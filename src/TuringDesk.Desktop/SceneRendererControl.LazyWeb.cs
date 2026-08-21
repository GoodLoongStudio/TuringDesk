using Microsoft.Web.WebView2.Wpf;
using System.Windows;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private WebView2? _webPlayer;

    private WebView2 WebPlayer => EnsureWebPlayer();

    private WebView2 EnsureWebPlayer()
    {
        if (_webPlayer is not null) return _webPlayer;

        var web = new WebView2
        {
            Visibility = Visibility.Collapsed
        };
        _webPlayer = web;
        WebPlayerHost.Children.Add(web);

        if (_propertyBridgeInitialized)
            web.NavigationCompleted += WebPlayer_PropertiesNavigationCompleted;

        EnsureWebPowerLifecycle();
        return web;
    }

    private void ShowWebPlayer()
    {
        var web = EnsureWebPlayer();
        WebPlayerHost.Visibility = Visibility.Visible;
        web.Visibility = Visibility.Visible;
    }

    private void HideWebPlayer()
    {
        WebPlayerHost.Visibility = Visibility.Collapsed;
        if (_webPlayer is not null)
            _webPlayer.Visibility = Visibility.Collapsed;
    }

    private void ReleaseWebPlayer()
    {
        var web = _webPlayer;
        if (web is null)
        {
            WebPlayerHost.Visibility = Visibility.Collapsed;
            return;
        }

        try { web.NavigationCompleted -= WebPlayer_NavigationCompleted; } catch { }
        if (_propertyBridgeInitialized)
        {
            try { web.NavigationCompleted -= WebPlayer_PropertiesNavigationCompleted; } catch { }
        }
        if (_webPowerLifecycleHooked)
        {
            try { web.IsVisibleChanged -= WebPlayer_IsVisibleChangedForPower; } catch { }
        }

        try
        {
            if (web.CoreWebView2 is not null)
                web.CoreWebView2.Navigate("about:blank");
        }
        catch { }

        try { WebPlayerHost.Children.Remove(web); } catch { }
        try
        {
            if (web is IDisposable disposable)
                disposable.Dispose();
        }
        catch { }

        _webPlayer = null;
        _webPowerLifecycleHooked = false;
        _webAudioBridgeInstalled = false;
        _webMessageHooked = false;
        _webAudioListenerRequested = false;
        WebPlayerHost.Visibility = Visibility.Collapsed;
    }
}
