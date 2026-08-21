using System.Text.Json;

namespace TuringDesk.Desktop.Services;

internal sealed class OnboardingState
{
    public bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class OnboardingStateStore
{
    private readonly string _path;

    public OnboardingStateStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuringDesk");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "onboarding.json");
    }

    public bool IsCompleted()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            return JsonSerializer.Deserialize<OnboardingState>(File.ReadAllText(_path))?.Completed == true;
        }
        catch
        {
            return false;
        }
    }

    public void Complete()
    {
        var state = new OnboardingState { Completed = true, CompletedAt = DateTimeOffset.UtcNow };
        File.WriteAllText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Reset()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
