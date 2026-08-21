namespace TuringDesk.Desktop.Services;

/// <summary>
/// Single model configuration center for TuringDesk L3 and official DeepSeek Harness.
/// L3 talks directly to the configured provider from the TuringDesk process; Harness
/// receives a synchronized copy of the same Provider/Base URL/Model/Credential state.
/// Saving configuration never starts Harness.
/// </summary>
public sealed class UnifiedModelConfigurationService
{
    private readonly ModelSettingsStore _store;

    public UnifiedModelConfigurationService(ModelSettingsStore store)
    {
        _store = store;
    }

    public static event Action<ModelSettings>? ConfigurationChanged;

    public async Task<ModelSettings> ApplyAndSaveAsync(ModelSettings settings, string? apiKey)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("请选择并填写一个真实模型配置。");

        var normalizedKey = apiKey?.Trim();
        var normalized = settings with
        {
            Mode = "direct",
            HasApiKey = !string.IsNullOrWhiteSpace(normalizedKey)
        };

        await _store.SaveAsync(normalized, normalizedKey);

        // Harness is a sibling consumer, not the L3 transport. Mirror the same state
        // into its writable stores without launching the workbench.
        HarnessModelBridgeService.Synchronize(normalized, normalizedKey);
        await HarnessWebUiService.ApplyModelSettingsAsync(normalized, normalizedKey);

        ConfigurationChanged?.Invoke(normalized);
        return normalized;
    }
}
