using System.Text.Json;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private readonly SceneInstanceSettingsStore _instanceSettingsStore = new();
    private bool _propertyBridgeInitialized;

    private void InitializePropertyBridge()
    {
        if (_propertyBridgeInitialized) return;
        _propertyBridgeInitialized = true;
        WebPlayer.NavigationCompleted += WebPlayer_PropertiesNavigationCompleted;
        SceneInstanceSettingsStore.SceneSettingsChanged += OnSceneSettingsChanged;
    }

    private void ShutdownPropertyBridge()
    {
        if (!_propertyBridgeInitialized) return;
        _propertyBridgeInitialized = false;
        WebPlayer.NavigationCompleted -= WebPlayer_PropertiesNavigationCompleted;
        SceneInstanceSettingsStore.SceneSettingsChanged -= OnSceneSettingsChanged;
    }

    private async void WebPlayer_PropertiesNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        await ApplyUserPropertiesToWebAsync();
    }

    private void OnSceneSettingsChanged(string sceneId)
    {
        if (_scene is null || !string.Equals(_scene.Id, sceneId, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(new Action(async () => await ApplyUserPropertiesAsync()));
    }

    private async Task ApplyUserPropertiesAsync()
    {
        if (_scene is null) return;
        var settings = _instanceSettingsStore.Load(_scene);
        SetVolume(settings.Volume, settings.Muted);
        if (_scene.Kind == SceneKind.Web) await ApplyUserPropertiesToWebAsync(settings);
    }

    private Task ApplyUserPropertiesToWebAsync()
    {
        if (_scene is null) return Task.CompletedTask;
        return ApplyUserPropertiesToWebAsync(_instanceSettingsStore.Load(_scene));
    }

    private async Task ApplyUserPropertiesToWebAsync(SceneInstanceSettings settings)
    {
        if (_scene?.Kind != SceneKind.Web || WebPlayer.CoreWebView2 is null) return;

        // Wallpaper Engine-compatible user-property shape: each key receives an
        // object with a `value` member. TuringDesk also emits its own event so a
        // web wallpaper can support both APIs without branching its entire app.
        var compatible = settings.Properties.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, object?> { ["value"] = pair.Value },
            StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(compatible);
        var script = $"""
            (() => {{
              const properties = {json};
              if (window.wallpaperPropertyListener && typeof window.wallpaperPropertyListener.applyUserProperties === 'function') {{
                window.wallpaperPropertyListener.applyUserProperties(properties);
              }}
              window.dispatchEvent(new CustomEvent('turingdesk-properties', {{ detail: properties }}));
            }})();
            """;
        try { await WebPlayer.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }
}
