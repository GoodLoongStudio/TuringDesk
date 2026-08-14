using System.Globalization;
using System.Speech.Recognition;

namespace TuringDesk.Desktop.Services;

public sealed class WindowsSpeechService : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private bool _disposed;

    public event Action<string, float>? Recognized;
    public event Action<string>? StatusChanged;

    public bool IsListening { get; private set; }
    public string RecognizerName { get; private set; } = "Unavailable";
    public string RecognizerCulture { get; private set; } = string.Empty;

    public Task<bool> StartAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsSpeechService));
        if (IsListening) return Task.FromResult(true);

        try
        {
            _engine ??= CreateEngine();
            if (_engine is null)
            {
                StatusChanged?.Invoke("No Windows speech recognizer installed");
                return Task.FromResult(false);
            }

            _engine.SetInputToDefaultAudioDevice();
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            IsListening = true;
            StatusChanged?.Invoke($"Listening · {RecognizerCulture}");
            return Task.FromResult(true);
        }
        catch (Exception error)
        {
            StatusChanged?.Invoke($"Voice unavailable · {error.Message}");
            return Task.FromResult(false);
        }
    }

    public Task StopAsync()
    {
        if (_engine is null || !IsListening) return Task.CompletedTask;

        try
        {
            _engine.RecognizeAsyncCancel();
            _engine.RecognizeAsyncStop();
        }
        catch
        {
            // Best effort: the recognizer may already be stopping.
        }

        IsListening = false;
        StatusChanged?.Invoke("Voice paused");
        return Task.CompletedTask;
    }

    private SpeechRecognitionEngine? CreateEngine()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0) return null;

        var current = CultureInfo.CurrentUICulture.Name;
        var preferred = installed.FirstOrDefault(item => item.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(item => item.Culture.Name.Equals(current, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(item => item.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? installed[0];

        var engine = new SpeechRecognitionEngine(preferred.Id);
        engine.LoadGrammar(new DictationGrammar { Name = "TuringDesk continuous dictation" });
        engine.SpeechRecognized += (_, args) =>
        {
            if (!IsListening || string.IsNullOrWhiteSpace(args.Result.Text)) return;
            Recognized?.Invoke(args.Result.Text.Trim(), args.Result.Confidence);
        };
        engine.SpeechRecognitionRejected += (_, _) => StatusChanged?.Invoke($"Listening · {RecognizerCulture}");
        engine.RecognizeCompleted += (_, _) =>
        {
            if (IsListening) StatusChanged?.Invoke($"Listening · {RecognizerCulture}");
        };

        RecognizerName = preferred.Description;
        RecognizerCulture = preferred.Culture.Name;
        return engine;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsListening = false;

        if (_engine is not null)
        {
            try { _engine.RecognizeAsyncCancel(); } catch { }
            _engine.Dispose();
            _engine = null;
        }
    }
}
