namespace TuringDesk.Desktop.Services;

/// <summary>
/// Single persistence path for TuringDesk + official Harness model stores.
/// Saving configuration is intentionally a cold operation: it must not spawn
/// Runtime/Harness. The explicit "测试连接" action or a real Agent request owns
/// connectivity validation and may acquire a Runtime lease.
/// </summary>
public sealed class UnifiedModelConfigurationService
{
    private readonly ModelSettingsStore _store;

    public UnifiedModelConfigurationService(RuntimeClient runtime, ModelSettingsStore store)
    {
        // Keep the existing constructor signature so current windows/callers do not
        // need a migration. Runtime is deliberately not touched by Save.
        _ = runtime;
        _store = store;
    }

    public async Task<ModelSettings> ApplyAndSaveAsync(ModelSettings settings, string? apiKey)
    {
        var normalizedKey = apiKey?.Trim();
        var normalized = settings with { HasApiKey = !string.IsNullOrWhiteSpace(normalizedKey) };

        await _store.SaveAsync(normalized, normalizedKey);
        await HarnessWebUiService.ApplyModelSettingsAsync(normalized, normalizedKey);
        return normalized;
    }
}
