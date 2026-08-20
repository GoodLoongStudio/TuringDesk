namespace TuringDesk.Desktop.Services;

/// <summary>
/// Single persistence path for TuringDesk + official Harness model stores.
/// Saving configuration is intentionally a cold operation: it must not spawn
/// Runtime or Harness. Connectivity testing is handled independently by
/// ModelConnectionProbeService.
/// </summary>
public sealed class UnifiedModelConfigurationService
{
    private readonly ModelSettingsStore _store;

    public UnifiedModelConfigurationService(ModelSettingsStore store)
    {
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
