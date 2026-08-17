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
        var pulse = animate ? 0.5f + 0.5f * MathF.Sin(t * 0.31f) : 0.5f;
        var drift = animate ? 0.5f + 0.5f * MathF.Sin(t * 0.21f + 1.7f) : 0.5f;
        var shimmer = animate ? 0.5f + 0.5f * MathF.Sin(t * 0.13f + 3.1f) : 0.5f;
        var audioBoost = _bass * 0.10f + _mid * 0.05f + _treble * 0.03f;
        var color = _preset.ToLowerInvariant() switch
        {
            "neon" => new Color4(
                Math.Min(1, 0.030f + 0.085f * pulse * _intensity + audioBoost * 0.42f),
                Math.Min(1, 0.012f + 0.050f * drift * _intensity + _treble * 0.045f),
                Math.Min(1, 0.095f + 0.140f * shimmer * _intensity + _bass * 0.085f),
                1.0f),
            "orbit" => new Color4(
                Math.Min(1, 0.020f + 0.050f * shimmer * _intensity + _mid * 0.030f),
                Math.Min(1, 0.035f + 0.075f * pulse * _intensity + _treble * 0.030f),
                Math.Min(1, 0.105f + 0.125f * drift * _intensity + _bass * 0.055f),
                1.0f),
            _ => new Color4(
                Math.Min(1, 0.028f + 0.070f * pulse * _intensity + _bass * 0.030f),
                Math.Min(1, 0.030f + 0.090f * drift * _intensity + _mid * 0.040f),
                Math.Min(1, 0.090f + 0.145f * shimmer * _intensity + audioBoost),
                1.0f)
        };

        e.Context.ClearRenderTargetView(target, color);
    }
}
