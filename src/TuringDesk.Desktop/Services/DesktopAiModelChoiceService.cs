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
/// Resolves the small model picker shown directly on the desktop search bar.
/// Ordering is intentional: the user's currently configured model wins, then a
/// working DeepSeek credential is offered as fallback, followed by setup.
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

        if (!current.ProviderId.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            var currentCredential = _store.LoadApiKey();
            var available = IsUsable(current, currentCredential);
            var preset = ModelProviderPresets.Find(current.ProviderId);
            choices.Add(new DesktopAiModelChoice(
                "configured",
                DisplayNameFor(current, preset),
                available ? "当前配置" : "当前配置缺少必要信息",
                current,
                currentCredential,
                available));
        }

        var deepSeekSettings = BuildDeepSeekSettings();
        var deepSeekCredential = HarnessModelBridgeService.LoadCredential(deepSeekSettings);
        if (string.IsNullOrWhiteSpace(deepSeekCredential) && current.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            deepSeekCredential = _store.LoadApiKey();

        var alreadyHasDeepSeek = choices.Any(choice =>
            choice.Settings?.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase) == true);

        if (!alreadyHasDeepSeek && !string.IsNullOrWhiteSpace(deepSeekCredential))
        {
            choices.Add(new DesktopAiModelChoice(
                "deepseek-fallback",
                "DeepSeek",
                deepSeekSettings.Model,
                deepSeekSettings with { HasApiKey = true },
                deepSeekCredential,
                true));
        }

        if (!choices.Any(choice => choice.IsAvailable))
        {
            choices.Insert(0, new DesktopAiModelChoice(
                "no-model",
                "⚠ 未配置 AI",
                "点击这里设置模型或 API Key",
                null,
                null,
                false,
                OpensSettings: true));
        }

        choices.Add(new DesktopAiModelChoice(
            "configure",
            "＋ 配置模型…",
            "打开 AI 模型设置",
            null,
            null,
            false,
            OpensSettings: true));

        return choices;
    }

    public DesktopAiModelChoice? ResolveDefault(IReadOnlyList<DesktopAiModelChoice> choices)
    {
        // The first usable entry is always the configured model when that model
        // is healthy enough to attempt, then DeepSeek. This matches the desktop
        // UX contract and keeps setup-only entries out of the default path.
        return choices.FirstOrDefault(choice => choice.IsAvailable)
               ?? choices.FirstOrDefault(choice => choice.Id == "no-model")
               ?? choices.FirstOrDefault();
    }

    private static bool IsUsable(ModelSettings settings, string? credential)
    {
        if (settings.ProviderId.Equals("mock", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(settings.Model)) return false;

        if (settings.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(credential);

        if (settings.ProviderId is "ollama" or "lmstudio" or "openai-compatible")
            return !string.IsNullOrWhiteSpace(settings.BaseUrl);

        var preset = ModelProviderPresets.Find(settings.ProviderId);
        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(credential)) return false;
        return !string.IsNullOrWhiteSpace(settings.BaseUrl) || !string.IsNullOrWhiteSpace(preset.BaseUrl);
    }

    private static ModelSettings BuildDeepSeekSettings()
    {
        var preset = ModelProviderPresets.Find("deepseek");
        return new ModelSettings(
            preset.Id,
            preset.Mode,
            preset.BaseUrl,
            preset.Model,
            false);
    }

    private static string DisplayNameFor(ModelSettings settings, ModelProviderPreset preset)
    {
        var provider = preset.Name
            .Replace(" API", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("（无需模型）", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var model = settings.Model.Trim();
        if (provider.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)) return "DeepSeek";
        if (string.IsNullOrWhiteSpace(model)) return provider;
        return $"{provider} · {model}";
    }
}
