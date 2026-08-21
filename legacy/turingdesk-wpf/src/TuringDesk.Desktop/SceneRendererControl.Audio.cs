using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private bool _audioLeaseActive;
    private bool _webAudioListenerRequested;
    private bool _webAudioBridgeInstalled;
    private bool _webMessageHooked;

    private void UpdateAudioLeaseForCurrentScene()
    {
        if (_paused || _stopped) return;

        var wantsAudio = _scene is not null &&
                         (_scene.AudioReactive ||
                          _webAudioListenerRequested ||
                          _scene.Layers.Any(layer => layer.AudioReactive || layer.Particle?.AudioReactive == true));

        if (wantsAudio && !_audioLeaseActive)
        {
            _audioLeaseActive = true;
            SystemAudioSpectrumService.Shared.SpectrumAvailable += OnAudioSpectrumAvailable;
            SystemAudioSpectrumService.Shared.Acquire();
        }
        else if (!wantsAudio && _audioLeaseActive)
        {
            ReleaseAudioBridge();
        }
    }

    private void ReleaseAudioBridge()
    {
        _scriptAudioFrame = null;
        GpuSurface.SetAudioLevel(0, 0, 0);
        if (!_audioLeaseActive) return;

        _audioLeaseActive = false;
        SystemAudioSpectrumService.Shared.SpectrumAvailable -= OnAudioSpectrumAvailable;
        SystemAudioSpectrumService.Shared.Release();
    }

    private void ShutdownAudioBridge()
    {
        ReleaseAudioBridge();
        _webAudioListenerRequested = false;
        if (_webMessageHooked && WebPlayer.CoreWebView2 is not null)
        {
            try { WebPlayer.CoreWebView2.WebMessageReceived -= WebPlayer_WebMessageReceived; } catch { }
        }
        _webMessageHooked = false;
    }

    private async Task InstallWebAudioCompatibilityBridgeAsync()
    {
        if (WebPlayer.CoreWebView2 is null) return;

        if (!_webAudioBridgeInstalled)
        {
            const string compatibilityScript = """
                (() => {
                  if (window.__turingDeskAudioBridgeInstalled) return;
                  window.__turingDeskAudioBridgeInstalled = true;
                  window.__turingDeskAudioListeners = [];
                  window.wallpaperRegisterAudioListener = function(callback) {
                    if (typeof callback !== 'function') return;
                    window.__turingDeskAudioListeners.push(callback);
                    if (window.chrome && window.chrome.webview) {
                      window.chrome.webview.postMessage({ type: 'turingdesk-audio-register' });
                    }
                  };
                  window.__turingDeskDispatchAudio = function(values) {
                    const listeners = window.__turingDeskAudioListeners.slice();
                    for (const callback of listeners) {
                      try { callback(values); } catch (_) { }
                    }
                  };
                })();
                """;
            await WebPlayer.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(compatibilityScript);
            _webAudioBridgeInstalled = true;
        }

        if (!_webMessageHooked)
        {
            WebPlayer.CoreWebView2.WebMessageReceived += WebPlayer_WebMessageReceived;
            _webMessageHooked = true;
        }
    }

    private void WebPlayer_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("type", out var type)) return;
            if (!string.Equals(type.GetString(), "turingdesk-audio-register", StringComparison.Ordinal)) return;
            _webAudioListenerRequested = true;
            UpdateAudioLeaseForCurrentScene();
        }
        catch
        {
            // Ignore unrelated or malformed web messages from wallpaper content.
        }
    }

    private void OnAudioSpectrumAvailable(AudioSpectrumFrame frame)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAudioSpectrumAvailable(frame)));
            return;
        }

        if (_scene is null || _paused || _stopped) return;
        _scriptAudioFrame = frame;
        GpuSurface.SetAudioLevel(frame.Bass, frame.Mid, frame.Treble);
        ApplyAudioToSceneGraph(frame);

        if (_scene.Kind == SceneKind.Web && _webAudioListenerRequested && WebPlayer.CoreWebView2 is not null)
        {
            var json = JsonSerializer.Serialize(frame.Values);
            _ = DispatchAudioToWebAsync(json);
        }
    }

    private void ApplyAudioToSceneGraph(AudioSpectrumFrame frame)
    {
        foreach (var pair in _sceneGraphElements)
        {
            if (!_sceneGraphLayers.TryGetValue(pair.Key, out var layer) || !layer.AudioReactive) continue;
            var strength = Math.Clamp(layer.AudioStrength, 0, 4);
            var energy = Math.Clamp(frame.Bass * 0.58 + frame.Mid * 0.30 + frame.Treble * 0.12, 0, 1.5);
            pair.Value.Opacity = Math.Clamp(layer.Opacity * (0.72 + energy * 0.55 * strength), 0, 1);

            if (pair.Value.RenderTransform is TransformGroup group &&
                group.Children.OfType<ScaleTransform>().FirstOrDefault() is { } scale)
            {
                var reactiveScale = layer.Scale * (1 + energy * 0.07 * strength);
                scale.ScaleX = reactiveScale;
                scale.ScaleY = reactiveScale;
            }
        }
    }

    private async Task DispatchAudioToWebAsync(string spectrumJson)
    {
        if (WebPlayer.CoreWebView2 is null) return;
        try
        {
            await WebPlayer.CoreWebView2.ExecuteScriptAsync(
                "if(window.__turingDeskDispatchAudio){window.__turingDeskDispatchAudio(" + spectrumJson + ");}");
        }
        catch { }
    }
}
