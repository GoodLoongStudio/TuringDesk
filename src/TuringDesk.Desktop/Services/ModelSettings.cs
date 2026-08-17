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
    public static ModelSettings Default => new("mock", "mock", string.Empty, string.Empty, false);
}

public static class ModelProviderPresets
{
    public static readonly ModelProviderPreset[] All =
    {
        new("mock", "Mock（无需模型）", "mock", string.Empty, string.Empty, false, "无需 API Key，用于安全测试桌面能力。真实模型全部由内置 DeepSeek Harness 驱动。"),
        new("deepseek", "DeepSeek API", "harness", "https://api.deepseek.com", "deepseek-v4-flash", true, "推荐。粘贴 DeepSeek API Key 一次，TuringDesk 与 Harness Models 页面会共用同一份凭据。"),
        new("ollama", "Ollama 本地模型", "harness", "http://127.0.0.1:11434/v1", string.Empty, false, "本机 Ollama OpenAI 兼容入口；只需填写模型 ID，通常无需 API Key。TuringDesk 与 Harness 共用这份配置。"),
        new("lmstudio", "LM Studio 本地模型", "harness", "http://127.0.0.1:1234/v1", string.Empty, false, "本机 LM Studio OpenAI 兼容服务；填写模型 ID，通常无需 API Key。TuringDesk 与 Harness 共用这份配置。"),
        new("openai-compatible", "OpenAI 兼容 API / 中转站", "harness", string.Empty, string.Empty, false, "填写 Base URL、模型 ID，按服务要求粘贴 API Key。保存后同步到 Harness 通用模型适配层。")
    };

    public static ModelProviderPreset Find(string? id) =>
        All.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
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
            return JsonSerializer.Deserialize<ModelSettings>(File.ReadAllText(_settingsPath)) ?? ModelSettings.Default;
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
            return await JsonSerializer.DeserializeAsync<ModelSettings>(stream) ?? ModelSettings.Default;
        }
        catch
        {
            return ModelSettings.Default;
        }
    }

    public string? LoadApiKey()
    {
        var settings = Load();
        var shared = HarnessModelBridgeService.LoadCredential(settings);
        if (!string.IsNullOrWhiteSpace(shared)) return shared;

        // Migration/fallback for installations created before v0.14.2. Once a
        // model is saved again, this copy is mirrored into Harness's official
        // credential store and both UIs use that shared state.
        return _credentials.Load();
    }

    public async Task SaveAsync(ModelSettings settings, string? apiKey)
    {
        var normalizedKey = apiKey?.Trim();
        var normalized = settings with { HasApiKey = !string.IsNullOrWhiteSpace(normalizedKey) };
        await using (var stream = File.Create(_settingsPath))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true });
        }

        if (string.IsNullOrWhiteSpace(normalizedKey)) _credentials.Delete();
        else _credentials.Save(normalizedKey);

        // Synchronize both official Harness settings and its writable credential
        // provider. Do not inject the key through the Harness process environment;
        // that source is read-only and would shadow the Models page.
        HarnessModelBridgeService.Synchronize(normalized, normalizedKey);
    }
}
