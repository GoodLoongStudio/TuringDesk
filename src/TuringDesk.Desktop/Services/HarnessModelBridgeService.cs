using System.Text;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Bridges TuringDesk's beginner-friendly model selection into the official
/// DeepSeek Harness settings/credential seams. Secrets never enter settings.yaml:
/// the API key stays in Windows Credential Manager and is injected only into the
/// owned Harness child process environment.
/// </summary>
public static class HarnessModelBridgeService
{
    public static string HarnessHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuringDesk",
        "Harness");

    public static string SettingsPath => Path.Combine(HarnessHome, "settings.yaml");

    public static void Synchronize(ModelSettings settings)
    {
        Directory.CreateDirectory(HarnessHome);

        var existing = File.Exists(SettingsPath)
            ? File.ReadAllText(SettingsPath, Encoding.UTF8)
            : string.Empty;

        // These are the only namespaces TuringDesk owns. Keep every other
        // Harness/WebUI/plugin setting untouched so advanced users can continue
        // using the official console without fighting the beginner settings UI.
        existing = RemoveTopLevelSection(existing, "llm-deepseek");
        existing = RemoveTopLevelSection(existing, "llm-pi-ai");

        var managed = BuildManagedSection(settings);
        var output = existing.TrimEnd();
        if (!string.IsNullOrWhiteSpace(managed))
        {
            if (output.Length > 0) output += Environment.NewLine + Environment.NewLine;
            output += managed.TrimEnd();
        }
        if (output.Length > 0) output += Environment.NewLine;

        File.WriteAllText(SettingsPath, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void ApplyEnvironment(ProcessStartInfo startInfo, ModelSettings settings, string? apiKey)
    {
        startInfo.Environment["DSH_HOME"] = HarnessHome;

        if (settings.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                startInfo.Environment["DEEPSEEK_API_KEY"] = apiKey.Trim();
            }
            if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                startInfo.Environment["DEEPSEEK_BASE_URL"] = settings.BaseUrl.Trim();
            }
            return;
        }

        if (settings.ProviderId is "openai-compatible" or "ollama" or "lmstudio")
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                startInfo.Environment["TURINGDESK_MODEL_API_KEY"] = apiKey.Trim();
            }
        }
    }

    private static string BuildManagedSection(ModelSettings settings)
    {
        if (settings.ProviderId.Equals("mock", StringComparison.OrdinalIgnoreCase)) return string.Empty;

        if (settings.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? "https://api.deepseek.com" : settings.BaseUrl.Trim();
            var model = string.IsNullOrWhiteSpace(settings.Model) ? "deepseek-v4-flash" : settings.Model.Trim();
            return $"""
llm-deepseek:
  apiKeyEnv: "DEEPSEEK_API_KEY"
  baseURL: {YamlString(baseUrl)}
  models:
    - id: {YamlString(model)}
      name: {YamlString(model)}
""";
        }

        if (settings.ProviderId is "openai-compatible" or "ollama" or "lmstudio")
        {
            var baseUrl = settings.BaseUrl.Trim();
            var model = settings.Model.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return string.Empty;
            var credentialLine = settings.HasApiKey
                ? "      apiKeyEnv: \"TURINGDESK_MODEL_API_KEY\"\n"
                : string.Empty;
            return $"""
llm-pi-ai:
  providers:
    turingdesk:
{credentialLine}      api: "openai-completions"
      baseURL: {YamlString(baseUrl)}
      models:
        - id: {YamlString(model)}
          name: {YamlString(model)}
""";
        }

        return string.Empty;
    }

    private static string YamlString(string value) => JsonSerializer.Serialize(value);

    private static string RemoveTopLevelSection(string text, string section)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var skipping = false;

        foreach (var line in lines)
        {
            var isTopLevel = line.Length > 0 && !char.IsWhiteSpace(line[0]);
            if (!skipping)
            {
                if (isTopLevel && line.Trim().Equals(section + ":", StringComparison.Ordinal))
                {
                    skipping = true;
                    continue;
                }
                kept.Add(line);
                continue;
            }

            if (isTopLevel && !line.TrimStart().StartsWith('#'))
            {
                skipping = false;
                kept.Add(line);
            }
            // Indented lines and blank/comment lines immediately following the
            // managed namespace belong to that namespace and are replaced.
        }

        return string.Join(Environment.NewLine, kept).TrimEnd();
    }
}
