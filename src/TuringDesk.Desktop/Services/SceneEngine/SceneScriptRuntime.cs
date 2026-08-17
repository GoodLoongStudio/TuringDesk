using Jint;
using Jint.Runtime;

namespace TuringDesk.Desktop.Services.SceneEngine;

internal sealed record SceneScriptFrame(
    double TimeSeconds,
    double DeltaSeconds,
    double PointerX,
    double PointerY,
    bool PointerDown,
    double Bass,
    double Mid,
    double Treble,
    double Rms);

/// <summary>
/// Sandboxed SceneScript host. CLR access is never enabled. Scripts receive only
/// primitive frame globals plus narrowly-scoped layer/property callbacks supplied
/// by the renderer. Each invocation is constrained so a broken downloaded scene
/// cannot freeze the desktop indefinitely.
/// </summary>
internal sealed class SceneScriptRuntime : IDisposable
{
    private readonly Engine _engine;
    private readonly Action<string, string, double> _setLayerNumber;
    private readonly Func<string, string, double> _getLayerNumber;
    private readonly Func<string, double> _getNumberProperty;
    private readonly Func<string, string> _getTextProperty;
    private readonly Func<string, bool> _getBoolProperty;
    private bool _hasInit;
    private bool _hasUpdate;
    private bool _initialized;
    private bool _faulted;

    public SceneScriptRuntime(
        string source,
        Action<string, string, double> setLayerNumber,
        Func<string, string, double> getLayerNumber,
        Func<string, double> getNumberProperty,
        Func<string, string> getTextProperty,
        Func<string, bool> getBoolProperty)
    {
        _setLayerNumber = setLayerNumber;
        _getLayerNumber = getLayerNumber;
        _getNumberProperty = getNumberProperty;
        _getTextProperty = getTextProperty;
        _getBoolProperty = getBoolProperty;

        _engine = new Engine(options =>
        {
            options.Strict();
            options.LimitMemory(8_000_000);
            options.TimeoutInterval(TimeSpan.FromMilliseconds(6));
            options.MaxStatements(8_000);
            options.LimitRecursion(48);
        });

        _engine.SetValue("setLayer", new Action<string, string, double>((layerId, property, value) =>
            _setLayerNumber(layerId, property, value)));
        _engine.SetValue("getLayer", new Func<string, string, double>((layerId, property) =>
            _getLayerNumber(layerId, property)));
        _engine.SetValue("getNumberProperty", new Func<string, double>(key => _getNumberProperty(key)));
        _engine.SetValue("getTextProperty", new Func<string, string>(key => _getTextProperty(key)));
        _engine.SetValue("getBoolProperty", new Func<string, bool>(key => _getBoolProperty(key)));

        try
        {
            _engine.Execute(source);
            _hasInit = _engine.GetValue("init").IsCallable;
            _hasUpdate = _engine.GetValue("update").IsCallable;
        }
        catch (Exception error) when (error is JavaScriptException or TimeoutException or MemoryLimitExceededException or StatementsCountOverflowException or RecursionDepthOverflowException)
        {
            _faulted = true;
            LastError = error.Message;
        }
    }

    public string? LastError { get; private set; }
    public bool IsFaulted => _faulted;

    public bool Tick(SceneScriptFrame frame)
    {
        if (_faulted) return false;
        try
        {
            _engine.SetValue("time", frame.TimeSeconds);
            _engine.SetValue("deltaTime", frame.DeltaSeconds);
            _engine.SetValue("pointerX", frame.PointerX);
            _engine.SetValue("pointerY", frame.PointerY);
            _engine.SetValue("pointerDown", frame.PointerDown);
            _engine.SetValue("audioBass", frame.Bass);
            _engine.SetValue("audioMid", frame.Mid);
            _engine.SetValue("audioTreble", frame.Treble);
            _engine.SetValue("audioRms", frame.Rms);

            if (!_initialized)
            {
                _initialized = true;
                if (_hasInit) _engine.Invoke("init");
            }
            if (_hasUpdate) _engine.Invoke("update");
            return true;
        }
        catch (Exception error) when (error is JavaScriptException or TimeoutException or MemoryLimitExceededException or StatementsCountOverflowException or RecursionDepthOverflowException)
        {
            _faulted = true;
            LastError = error.Message;
            return false;
        }
    }

    public void Dispose()
    {
        // Jint Engine itself does not own unmanaged resources. Dropping the
        // engine releases the isolated script heap for garbage collection.
    }
}
