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
        new("deepseek", "DeepSeek API", "harness", "https://api.deepseek.com", "deepseek-v4-flash", true, "推荐。粘贴 DeepSeek API Key 一次，TuringDesk 会同步到内置 Harness，并自动启动 Agent + Windows MCP。"),
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

    public string? LoadApiKey() => _credentials.Load();

    public async Task SaveAsync(ModelSettings settings, string? apiKey)
    {
        var normalized = settings with { HasApiKey = !string.IsNullOrWhiteSpace(apiKey) };
        await using (var stream = File.Create(_settingsPath))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true });
        }

        if (string.IsNullOrWhiteSpace(apiKey)) _credentials.Delete();
        else _credentials.Save(apiKey.Trim());

        // Keep the official Harness settings document in lock-step with the
        // beginner TuringDesk settings. The secret itself remains only in
        // Windows Credential Manager and is injected into the child process.
        HarnessModelBridgeService.Synchronize(normalized);
    }
}
