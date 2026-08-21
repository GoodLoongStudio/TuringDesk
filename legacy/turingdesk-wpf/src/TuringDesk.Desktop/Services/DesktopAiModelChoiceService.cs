namespace TuringDesk.Desktop.Services;

public sealed record DesktopAiModelChoice(
    string Id,
    string DisplayName,
    string Detail,
    ModelSettings? Settings,
    string? Credential,
    bool IsAvailable,
    bool OpensSettings = false)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Resolves the model picker shown directly on the desktop search bar. The model
/// configuration center is the single source of truth; no mock/provider surrogate
/// appears in the product UI.
/// </summary>
public sealed class DesktopAiModelChoiceService
{
    private readonly ModelSettingsStore _store;

    public DesktopAiModelChoiceService(ModelSettingsStore store)
    {
        _store = store;
    }

    public IReadOnlyList<DesktopAiModelChoice> LoadChoices()
    {
        var choices = new List<DesktopAiModelChoice>();
        var current = _store.Load();

        if (current.IsConfigured)
        {
            var currentCredential = _store.LoadApiKey();
            var available = IsUsable(current, currentCredential);
            var preset = ModelProviderPresets.Find(current.ProviderId);
            var providerLabel = ProviderLabel(preset);
            choices.Add(new DesktopAiModelChoice(
                "configured",
                DisplayNameFor(current, preset),
                available ? $"{providerLabel} · 当前配置" : $"{providerLabel} · 缺少必要信息",
                current,
                currentCredential,
                available));
        }

        if (!choices.Any(choice => choice.IsAvailable))
        {
            choices.Add(new DesktopAiModelChoice(
                "no-model",
                "未配置 AI",
                "打开模型配置中心，连接 DeepSeek、本地模型或 OpenAI-compatible API",
                null,
                null,
                false,
                OpensSettings: true));
        }

        choices.Add(new DesktopAiModelChoice(
            "configure",
            "模型配置中心…",
            "切换 Provider、Base URL、Model 与 API Key",
            null,
            null,
            false,
            OpensSettings: true));

        return choices;
    }

    public DesktopAiModelChoice? ResolveDefault(IReadOnlyList<DesktopAiModelChoice> choices)
    {
        return choices.FirstOrDefault(choice => choice.IsAvailable)
               ?? choices.FirstOrDefault(choice => choice.Id == "no-model")
               ?? choices.FirstOrDefault();
    }

    private static bool IsUsable(ModelSettings settings, string? credential)
    {
        if (!settings.IsConfigured) return false;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl)) return false;

        var preset = ModelProviderPresets.Find(settings.ProviderId);
        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(credential)) return false;
        return true;
    }

    private static string DisplayNameFor(ModelSettings settings, ModelProviderPreset preset)
    {
        var model = settings.Model.Trim();
        return !string.IsNullOrWhiteSpace(model) ? model : ProviderLabel(preset);
    }

    private static string ProviderLabel(ModelProviderPreset preset) => preset.Name
        .Replace(" API", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim();
}
