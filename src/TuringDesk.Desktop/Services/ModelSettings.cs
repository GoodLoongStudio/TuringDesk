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
        new("mock", "Mock（无需模型）", "mock", string.Empty, string.Empty, false, "无需 API Key，用于安全测试桌面能力。"),
        new("deepseek", "DeepSeek API", "openai-compatible", "https://api.deepseek.com", "deepseek-v4-flash", true, "粘贴 DeepSeek API Key 即可，保存后立即用于普通对话。"),
        new("ollama", "Ollama 本地模型", "openai-compatible", "http://127.0.0.1:11434/v1", string.Empty, false, "本机 Ollama OpenAI 兼容入口；填模型 ID，通常无需 API Key。"),
        new("lmstudio", "LM Studio 本地模型", "openai-compatible", "http://127.0.0.1:1234/v1", string.Empty, false, "本机 LM Studio OpenAI 兼容服务；填模型 ID，通常无需 API Key。"),
        new("openai-compatible", "OpenAI 兼容 API / 中转站", "openai-compatible", string.Empty, string.Empty, false, "填写 Base URL、模型 ID，API Key 按服务要求填写。"),
        new("deepseek-harness", "DeepSeek Harness（高级）", "harness", "https://api.deepseek.com", "deepseek-v4-flash", true, "完整 Agent + MCP 模式；需要配置 TURINGDESK_HARNESS_COMMAND 指向 Harness Runtime。")
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
    }
}
