using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TuringDesk.Desktop.Services.SceneEngine;

namespace TuringDesk.Desktop;

public partial class SceneRendererControl
{
    private readonly DispatcherTimer _desktopInputTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private DesktopInputSnapshot? _lastInput;
    private bool _desktopInputHooked;

    private void Renderer_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_desktopInputHooked)
        {
            _desktopInputTimer.Tick += DesktopInputTimer_Tick;
            _desktopInputHooked = true;
        }
        _desktopInputTimer.Start();
    }

    private void Renderer_Unloaded(object sender, RoutedEventArgs e)
    {
        _desktopInputTimer.Stop();
    }

    private async void DesktopInputTimer_Tick(object? sender, EventArgs e)
    {
        if (_scene is null || !_scene.Interactive || _paused || _stopped) return;
        var input = DesktopInputBridge.Capture();
        if (input is null) return;

        if (_scene.Kind == SceneKind.Web)
        {
            var moved = _lastInput is null || Math.Abs(input.X - _lastInput.X) >= 1 || Math.Abs(input.Y - _lastInput.Y) >= 1;
            var buttonChanged = _lastInput is null || input.LeftDown != _lastInput.LeftDown;
            if (moved || buttonChanged) await DispatchWebInputAsync(input, buttonChanged);
        }
        else if (_scene.Kind == SceneKind.Scene)
        {
            ApplySceneParallax(input);
            ApplySceneGraphParallax(input);
        }

        _lastInput = input;
    }

    private void ApplySceneParallax(DesktopInputSnapshot input)
    {
        if (_scene is { Layers.Count: > 0 }) return;
        var x = (input.NormalizedX - 0.5) * -18;
        var y = (input.NormalizedY - 0.5) * -14;
        var transform = AuroraLayer.RenderTransform as TranslateTransform ?? new TranslateTransform();
        AuroraLayer.RenderTransform = transform;
        transform.X = x;
        transform.Y = y;
    }

    private async Task DispatchWebInputAsync(DesktopInputSnapshot input, bool buttonChanged)
    {
        if (WebPlayer.CoreWebView2 is null) return;
        var nx = input.NormalizedX.ToString("0.######", CultureInfo.InvariantCulture);
        var ny = input.NormalizedY.ToString("0.######", CultureInfo.InvariantCulture);
        var down = input.LeftDown ? "true" : "false";
        var change = buttonChanged ? "true" : "false";
        var script = $"""
            (() => {{
              const nx = {nx}, ny = {ny}, down = {down}, changed = {change};
              const x = Math.max(0, Math.min(window.innerWidth - 1, nx * window.innerWidth));
              const y = Math.max(0, Math.min(window.innerHeight - 1, ny * window.innerHeight));
              const target = document.elementFromPoint(x, y) || document.body || document.documentElement;
              target.dispatchEvent(new MouseEvent('mousemove', {{clientX:x, clientY:y, bubbles:true, cancelable:true, view:window}}));
              if (changed) target.dispatchEvent(new MouseEvent(down ? 'mousedown' : 'mouseup', {{clientX:x, clientY:y, button:0, buttons:down ? 1 : 0, bubbles:true, cancelable:true, view:window}}));
              window.dispatchEvent(new CustomEvent('turingdesk-input', {{detail: {{x, y, normalizedX:nx, normalizedY:ny, leftDown:down}}}}));
            }})();
            """;
        try { await WebPlayer.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }
}
