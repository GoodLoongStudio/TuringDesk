using System.Diagnostics;
using Vortice.Mathematics;
using Vortice.Wpf;

namespace TuringDesk.Desktop;

/// <summary>
/// Real Direct3D11 render surface for the desktop Scene backend. This is the GPU
/// foundation that SceneGraph image/particle/effect passes migrate onto. WPF is
/// retained above it as a compatibility/editor layer while that migration lands.
/// </summary>
public sealed class GpuSceneSurface : DrawingSurface
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _preset = "aurora";
    private float _intensity = 0.85f;
    private bool _motion = true;
    private bool _paused;
    private float _bass;
    private float _mid;
    private float _treble;

    public GpuSceneSurface()
    {
        AlwaysRefresh = true;
        Draw += OnDraw;
    }

    public void Configure(string? preset, double intensity, bool motion)
    {
        _preset = string.IsNullOrWhiteSpace(preset) ? "aurora" : preset;
        _intensity = (float)Math.Clamp(intensity, 0.2, 1.0);
        _motion = motion;
        _paused = false;
        Invalidate();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Invalidate();
    }

    public void SetAudioLevel(float bass, float mid, float treble)
    {
        _bass = Math.Clamp(bass, 0, 1.5f);
        _mid = Math.Clamp(mid, 0, 1.5f);
        _treble = Math.Clamp(treble, 0, 1.5f);
        Invalidate();
    }

    private void OnDraw(object? sender, DrawEventArgs e)
    {
        var target = e.Surface.ColorTextureView;
        if (target is null) return;

        var animate = _motion && !_paused;
        var t = animate ? (float)_clock.Elapsed.TotalSeconds : 0f;
        var pulse = animate ? 0.5f + 0.5f * MathF.Sin(t * 0.23f) : 0.5f;
        var drift = animate ? 0.5f + 0.5f * MathF.Sin(t * 0.17f + 1.7f) : 0.5f;
        var audioBoost = _bass * 0.09f + _mid * 0.045f + _treble * 0.025f;
        var color = _preset.ToLowerInvariant() switch
        {
            "neon" => new Color4(
                Math.Min(1, 0.022f + 0.035f * pulse * _intensity + audioBoost * 0.45f),
                Math.Min(1, 0.014f + 0.025f * drift * _intensity + _treble * 0.04f),
                Math.Min(1, 0.075f + 0.095f * pulse * _intensity + _bass * 0.08f),
                1.0f),
            "orbit" => new Color4(
                Math.Min(1, 0.018f + 0.025f * drift * _intensity + _mid * 0.025f),
                Math.Min(1, 0.027f + 0.040f * pulse * _intensity + _treble * 0.025f),
                Math.Min(1, 0.064f + 0.070f * drift * _intensity + _bass * 0.05f),
                1.0f),
            _ => new Color4(
                Math.Min(1, 0.018f + 0.025f * pulse * _intensity + _bass * 0.025f),
                Math.Min(1, 0.024f + 0.045f * drift * _intensity + _mid * 0.035f),
                Math.Min(1, 0.052f + 0.085f * pulse * _intensity + audioBoost),
                1.0f)
        };

        e.Context.ClearRenderTargetView(target, color);
    }
}
