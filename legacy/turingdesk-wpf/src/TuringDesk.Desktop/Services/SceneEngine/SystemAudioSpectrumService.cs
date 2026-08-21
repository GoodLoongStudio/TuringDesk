using System.Diagnostics;
using NAudio.Wave;

namespace TuringDesk.Desktop.Services.SceneEngine;

internal sealed record AudioSpectrumFrame(float[] Values, float Bass, float Mid, float Treble, float Rms);

/// <summary>
/// Captures the default Windows render endpoint through WASAPI loopback and
/// produces the Wallpaper Engine web-audio layout: 64 frequency bins for the
/// left channel followed by 64 bins for the right channel, roughly 30 Hz.
/// </summary>
internal sealed class SystemAudioSpectrumService : IDisposable
{
    private const int WindowSize = 1024;
    private const int BinsPerChannel = 64;
    private static readonly float[] Hann = CreateHannWindow();

    private readonly object _gate = new();
    private readonly float[] _leftRing = new float[WindowSize];
    private readonly float[] _rightRing = new float[WindowSize];
    private readonly float[] _smoothed = new float[BinsPerChannel * 2];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private WasapiLoopbackCapture? _capture;
    private int _ringWrite;
    private int _sampleRate = 48000;
    private int _leaseCount;
    private long _lastAnalysisMs;
    private int _analysisInFlight;
    private int _restartScheduled;
    private bool _disposed;

    public static SystemAudioSpectrumService Shared { get; } = new();

    public event Action<AudioSpectrumFrame>? SpectrumAvailable;

    public bool IsRunning
    {
        get { lock (_gate) return _capture is not null; }
    }

    public void Acquire()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _leaseCount++;
            if (_capture is not null) return;
            _ = TryStartCaptureNoLock();
        }
    }

    public void Release()
    {
        WasapiLoopbackCapture? capture = null;
        lock (_gate)
        {
            if (_leaseCount > 0) _leaseCount--;
            if (_leaseCount != 0 || _capture is null) return;
            capture = _capture;
            _capture = null;
            DetachCaptureHandlers(capture);
        }

        StopAndDispose(capture);
    }

    private bool TryStartCaptureNoLock()
    {
        if (_disposed || _leaseCount <= 0 || _capture is not null) return false;

        WasapiLoopbackCapture? capture = null;
        try
        {
#pragma warning disable CS0618
            capture = new WasapiLoopbackCapture();
#pragma warning restore CS0618
            _sampleRate = Math.Max(8000, capture.WaveFormat.SampleRate);
            capture.DataAvailable += Capture_DataAvailable;
            capture.RecordingStopped += Capture_RecordingStopped;
            _capture = capture;
            capture.StartRecording();
            return true;
        }
        catch
        {
            if (capture is not null)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
                DetachCaptureHandlers(capture);
                try { capture.Dispose(); } catch { }
            }
            return false;
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || e.BytesRecorded <= 0) return;
        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var bytesPerChannelSample = Math.Max(1, format.BitsPerSample / 8);
        var frameBytes = Math.Max(bytesPerChannelSample * channels, format.BlockAlign);
        if (frameBytes <= 0) return;

        lock (_gate)
        {
            if (!ReferenceEquals(_capture, capture) || _disposed || _leaseCount <= 0) return;

            for (var offset = 0; offset + frameBytes <= e.BytesRecorded; offset += frameBytes)
            {
                var left = ReadSample(e.Buffer, offset, bytesPerChannelSample);
                var right = channels > 1
                    ? ReadSample(e.Buffer, offset + bytesPerChannelSample, bytesPerChannelSample)
                    : left;
                _leftRing[_ringWrite] = left;
                _rightRing[_ringWrite] = right;
                _ringWrite = (_ringWrite + 1) % WindowSize;
            }
        }

        var now = _clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _lastAnalysisMs) < 32) return;
        if (Interlocked.CompareExchange(ref _analysisInFlight, 1, 0) != 0) return;
        Interlocked.Exchange(ref _lastAnalysisMs, now);

        var leftCopy = new float[WindowSize];
        var rightCopy = new float[WindowSize];
        int sampleRate;
        lock (_gate)
        {
            if (_disposed || _leaseCount <= 0)
            {
                Volatile.Write(ref _analysisInFlight, 0);
                return;
            }

            sampleRate = _sampleRate;
            for (var i = 0; i < WindowSize; i++)
            {
                var source = (_ringWrite + i) % WindowSize;
                leftCopy[i] = _leftRing[source];
                rightCopy[i] = _rightRing[source];
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed || _leaseCount <= 0) return;
                }
                Analyze(leftCopy, rightCopy, sampleRate);
            }
            finally
            {
                Volatile.Write(ref _analysisInFlight, 0);
            }
        });
    }

    private void Analyze(float[] left, float[] right, int sampleRate)
    {
        var values = new float[BinsPerChannel * 2];
        AnalyzeChannel(left, sampleRate, values, 0);
        AnalyzeChannel(right, sampleRate, values, BinsPerChannel);

        lock (_gate)
        {
            if (_disposed || _leaseCount <= 0) return;
            for (var i = 0; i < values.Length; i++)
            {
                var alpha = values[i] > _smoothed[i] ? 0.58f : 0.22f;
                _smoothed[i] += (values[i] - _smoothed[i]) * alpha;
                values[i] = _smoothed[i];
            }
        }

        var bass = AverageStereo(values, 0, 10);
        var mid = AverageStereo(values, 10, 34);
        var treble = AverageStereo(values, 34, 64);
        var rms = ComputeRms(left, right);

        lock (_gate)
        {
            if (_disposed || _leaseCount <= 0) return;
        }
        SpectrumAvailable?.Invoke(new AudioSpectrumFrame(values, bass, mid, treble, rms));
    }

    private static void AnalyzeChannel(float[] samples, int sampleRate, float[] destination, int destinationOffset)
    {
        var nyquist = sampleRate / 2.0;
        var minHz = 28.0;
        var maxHz = Math.Max(minHz + 1, Math.Min(18000.0, nyquist * 0.94));
        var logMin = Math.Log(minHz);
        var logSpan = Math.Log(maxHz) - logMin;

        for (var bin = 0; bin < BinsPerChannel; bin++)
        {
            var normalized = bin / (double)(BinsPerChannel - 1);
            var frequency = Math.Exp(logMin + logSpan * normalized);
            var angle = -2.0 * Math.PI * frequency / sampleRate;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var oscRe = 1.0;
            var oscIm = 0.0;
            double re = 0;
            double im = 0;

            for (var i = 0; i < WindowSize; i++)
            {
                var sample = samples[i] * Hann[i];
                re += sample * oscRe;
                im += sample * oscIm;
                var nextRe = oscRe * cos - oscIm * sin;
                oscIm = oscRe * sin + oscIm * cos;
                oscRe = nextRe;
            }

            var magnitude = Math.Sqrt(re * re + im * im) * (2.0 / WindowSize);
            destination[destinationOffset + bin] = (float)Math.Min(2.5, Math.Pow(magnitude * 5.5, 0.72));
        }
    }

    private static float ReadSample(byte[] buffer, int offset, int bytesPerSample)
    {
        try
        {
            return bytesPerSample switch
            {
                1 => (buffer[offset] - 128) / 128f,
                2 => BitConverter.ToInt16(buffer, offset) / 32768f,
                3 => Read24Bit(buffer, offset),
                _ => Read32Bit(buffer, offset)
            };
        }
        catch
        {
            return 0;
        }
    }

    private static float Read24Bit(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((value & 0x00800000) != 0) value |= unchecked((int)0xFF000000);
        return value / 8388608f;
    }

    private static float Read32Bit(byte[] buffer, int offset)
    {
        var value = BitConverter.ToSingle(buffer, offset);
        if (float.IsFinite(value) && Math.Abs(value) <= 4f) return value;
        return BitConverter.ToInt32(buffer, offset) / 2147483648f;
    }

    private static float AverageStereo(float[] values, int start, int end)
    {
        double sum = 0;
        var count = 0;
        for (var i = start; i < end; i++)
        {
            sum += Math.Min(values[i], 1f);
            sum += Math.Min(values[i + BinsPerChannel], 1f);
            count += 2;
        }
        return count == 0 ? 0 : (float)(sum / count);
    }

    private static float ComputeRms(float[] left, float[] right)
    {
        double sum = 0;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * left[i] + right[i] * right[i];
        }
        return (float)Math.Min(1, Math.Sqrt(sum / (left.Length * 2)) * 3.2);
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture) return;

        var shouldRestart = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_capture, capture)) return;
            _capture = null;
            DetachCaptureHandlers(capture);
            shouldRestart = !_disposed && _leaseCount > 0;
        }

        try { capture.Dispose(); } catch { }
        if (shouldRestart) ScheduleRestart();
    }

    private void ScheduleRestart()
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || _leaseCount <= 0 || _capture is not null) return;
                    _ = TryStartCaptureNoLock();
                }
            }
            finally
            {
                Volatile.Write(ref _restartScheduled, 0);
            }
        });
    }

    private void DetachCaptureHandlers(WasapiLoopbackCapture capture)
    {
        try { capture.DataAvailable -= Capture_DataAvailable; } catch { }
        try { capture.RecordingStopped -= Capture_RecordingStopped; } catch { }
    }

    private static float[] CreateHannWindow()
    {
        var result = new float[WindowSize];
        for (var i = 0; i < result.Length; i++)
            result[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (result.Length - 1)));
        return result;
    }

    private static void StopAndDispose(WasapiLoopbackCapture capture)
    {
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); } catch { }
    }

    public void Dispose()
    {
        WasapiLoopbackCapture? capture;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _leaseCount = 0;
            capture = _capture;
            _capture = null;
            if (capture is not null) DetachCaptureHandlers(capture);
        }
        if (capture is not null) StopAndDispose(capture);
    }
}
