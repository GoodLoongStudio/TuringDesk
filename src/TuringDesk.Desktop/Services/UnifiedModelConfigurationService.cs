namespace TuringDesk.Desktop.Services;

/// <summary>
/// The single write path for model configuration in TuringDesk.
/// Beginner onboarding and later model changes must both use this service so
/// Runtime, persistent TuringDesk settings and the official Harness
/// settings/credential stores cannot drift apart.
/// </summary>
public sealed class UnifiedModelConfigurationService
{
    private readonly RuntimeClient _runtime;
    private readonly ModelSettingsStore _store;

    public UnifiedModelConfigurationService(RuntimeClient runtime, ModelSettingsStore store)
    {
        _runtime = runtime;
        _store = store;
    }

    public async Task<ModelSettings> ApplyAndSaveAsync(ModelSettings settings, string? apiKey)
    {
        var normalizedKey = apiKey?.Trim();
        var normalized = settings with { HasApiKey = !string.IsNullOrWhiteSpace(normalizedKey) };

        // Validate the real quick-Agent path first. A broken model endpoint should
        // not replace the last working persisted configuration.
        var configured = await _runtime.ConfigureModelAsync(normalized, normalizedKey);
        if (configured is null)
        {
            throw new InvalidOperationException("TuringDesk Runtime 没有接受模型配置。请检查模型、Base URL 和 API Key。");
        }

        // ModelSettingsStore mirrors into the official Harness settings and
        // .credentials.yaml. HarnessWebUiService then guarantees the already-
        // booting/booted official Web profile sees the same stores.
        await _store.SaveAsync(normalized, normalizedKey);
        await HarnessWebUiService.ApplyModelSettingsAsync(normalized, normalizedKey);
        return normalized;
    }
}
