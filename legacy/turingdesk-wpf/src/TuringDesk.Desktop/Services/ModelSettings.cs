using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed record ModelProviderPreset(
    string Id,
    string Name,
    string Mode,
    string BaseUrl,
    string Model,
    bool RequiresApiKey,
    string Hint);

public sealed record ModelSettings(
    string ProviderId,
    string Mode,
    string BaseUrl,
    string Model,
    bool HasApiKey)
{
    public static ModelSettings Default => new("unconfigured", "direct", string.Empty, "未配置", false);

    public bool IsConfigured =>
        !ProviderId.Equals("unconfigured", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Model);
}

public static class ModelProviderPresets
{
    private static readonly ModelProviderPreset Unconfigured = new(
        "unconfigured",
        "尚未连接真实模型",
        "direct",
        string.Empty,
        "未配置",
        false,
        "请在模型配置中心选择一个真实模型来源。");

    public static readonly ModelProviderPreset[] All =
    {
        new("deepseek", "DeepSeek API", "direct", "https://api.deepseek.com", "deepseek-v4-flash", true, "TuringDesk L3 由原生 HttpClient 直接调用 DeepSeek；保存后同一份配置与凭据同步给 DeepSeek Harness。"),
        new("ollama", "Ollama 本地模型", "direct", "http://127.0.0.1:11434/v1", string.Empty, false, "TuringDesk L3 直接调用本机 Ollama 的 OpenAI 兼容接口；通常无需 API Key。"),
        new("lmstudio", "LM Studio 本地模型", "direct", "http://127.0.0.1:1234/v1", string.Empty, false, "TuringDesk L3 直接调用本机 LM Studio 的 OpenAI 兼容接口；通常无需 API Key。"),
        new("openai-compatible", "OpenAI 兼容 API / 中转站", "direct", string.Empty, string.Empty, false, "填写 Base URL、模型 ID 和服务要求的 API Key。L3 直接调用；保存后同步给 Harness。")
    };

    public static ModelProviderPreset Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("unconfigured", StringComparison.OrdinalIgnoreCase))
            return Unconfigured;
        return All.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }
}

public sealed class ModelSettingsStore
{
    private readonly WindowsCredentialStore _credentials = new();
    private readonly string _settingsPath;

    public ModelSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuringDesk");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "model-settings.json");
    }

    public ModelSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return ModelSettings.Default;
            var loaded = JsonSerializer.Deserialize<ModelSettings>(File.ReadAllText(_settingsPath)) ?? ModelSettings.Default;
            return Normalize(loaded);
        }
        catch
        {
            return ModelSettings.Default;
        }
    }

    public async Task<ModelSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return ModelSettings.Default;
            await using var stream = File.OpenRead(_settingsPath);
            var loaded = await JsonSerializer.DeserializeAsync<ModelSettings>(stream) ?? ModelSettings.Default;
            return Normalize(loaded);
        }
        catch
        {
            return ModelSettings.Default;
        }
    }

    public string? LoadApiKey()
    {
        var settings = Load();
        if (!settings.IsConfigured) return null;

        var shared = HarnessModelBridgeService.LoadCredential(settings);
        if (!string.IsNullOrWhiteSpace(shared)) return shared;

        // Migration/recovery copy for installations that predate the shared Harness
        // credential store, and a Windows-native fallback if Harness state is absent.
        return _credentials.Load();
    }

    public async Task SaveAsync(ModelSettings settings, string? apiKey)
    {
        var normalizedKey = apiKey?.Trim();
        var normalized = Normalize(settings) with { HasApiKey = !string.IsNullOrWhiteSpace(normalizedKey) };
        await using (var stream = File.Create(_settingsPath))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true });
        }

        if (string.IsNullOrWhiteSpace(normalizedKey)) _credentials.Delete();
        else _credentials.Save(normalizedKey);
    }

    private static ModelSettings Normalize(ModelSettings settings)
    {
        // Migrate the removed development-only placeholder state into a real
        // unconfigured state. There is no mock provider or mock execution path.
        if (settings.ProviderId.Equals("mock", StringComparison.OrdinalIgnoreCase) ||
            settings.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase))
            return ModelSettings.Default;

        if (settings.ProviderId.Equals("unconfigured", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(settings.ProviderId))
            return ModelSettings.Default;

        // Older builds described real providers as "harness" mode. L3 now always
        // calls providers directly while Harness is a sibling consumer of the same state.
        var mode = settings.Mode.Equals("harness", StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : string.IsNullOrWhiteSpace(settings.Mode) ? "direct" : settings.Mode;

        return settings with { Mode = mode };
    }
}
