using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Bridges TuringDesk's beginner-friendly model selection into the official
/// DeepSeek Harness settings and credentials seams.
///
/// The official Harness local credential provider uses
/// $DSH_HOME/.credentials.yaml. TuringDesk writes that same store instead of
/// shadowing it with process environment variables, so the beginner setup and
/// Harness Models page see one credential state and Harness can hot-reload it.
/// </summary>
public static class HarnessModelBridgeService
{
    private const string DeepSeekCredentialRef = "DEEPSEEK_API_KEY";
    private const string CompatibleCredentialRef = "TURINGDESK_MODEL_API_KEY";

    public static string HarnessHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuringDesk",
        "Harness");

    public static string SettingsPath => Path.Combine(HarnessHome, "settings.yaml");
    public static string CredentialsPath => Path.Combine(HarnessHome, ".credentials.yaml");

    public static void Synchronize(ModelSettings settings) => SynchronizeSettings(settings);

    public static void Synchronize(ModelSettings settings, string? apiKey)
    {
        SynchronizeSettings(settings);
        SynchronizeCredentials(settings, apiKey);
    }

    public static string? LoadCredential(ModelSettings settings)
    {
        var reference = CredentialReference(settings);
        if (reference is null || !File.Exists(CredentialsPath)) return null;

        try
        {
            foreach (var line in File.ReadLines(CredentialsPath, Encoding.UTF8))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                if (!trimmed.StartsWith(reference + ":", StringComparison.Ordinal)) continue;

                var raw = trimmed[(reference.Length + 1)..].Trim();
                if (raw.Length == 0) return null;
                return ParseYamlScalar(raw);
            }
        }
        catch
        {
            // The Harness provider owns validation and diagnostics. TuringDesk
            // simply falls back to its Windows credential migration copy.
        }

        return null;
    }

    /// <summary>
    /// Only points the child process at the shared Harness home. Provider keys
    /// deliberately do NOT enter the process environment: Harness treats an
    /// inherited environment credential as read-only and it shadows the writable
    /// Models-page credential store.
    /// </summary>
    public static void ApplyEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["DSH_HOME"] = HarnessHome;
        startInfo.Environment.Remove(DeepSeekCredentialRef);
        startInfo.Environment.Remove(CompatibleCredentialRef);
        startInfo.Environment.Remove("DEEPSEEK_BASE_URL");
    }

    private static void SynchronizeSettings(ModelSettings settings)
    {
        Directory.CreateDirectory(HarnessHome);

        var existing = File.Exists(SettingsPath)
            ? File.ReadAllText(SettingsPath, Encoding.UTF8)
            : string.Empty;

        // These are the only settings namespaces TuringDesk owns. Keep every
        // other Harness/WebUI/plugin setting untouched.
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

    private static void SynchronizeCredentials(ModelSettings settings, string? apiKey)
    {
        Directory.CreateDirectory(HarnessHome);

        var existing = File.Exists(CredentialsPath)
            ? File.ReadAllLines(CredentialsPath, Encoding.UTF8).ToList()
            : new List<string>();

        // TuringDesk manages only these two references. Leave credentials for
        // other Harness providers exactly as the user/Models page stored them.
        existing.RemoveAll(line => IsCredentialLine(line, DeepSeekCredentialRef)
                                   || IsCredentialLine(line, CompatibleCredentialRef));

        var reference = CredentialReference(settings);
        var normalizedKey = apiKey?.Trim();
        if (reference is not null && !string.IsNullOrWhiteSpace(normalizedKey))
        {
            while (existing.Count > 0 && string.IsNullOrWhiteSpace(existing[^1])) existing.RemoveAt(existing.Count - 1);
            if (existing.Count > 0) existing.Add(string.Empty);
            existing.Add($"{reference}: {YamlString(normalizedKey)}");
        }

        while (existing.Count > 0 && string.IsNullOrWhiteSpace(existing[^1])) existing.RemoveAt(existing.Count - 1);

        if (existing.Count == 0)
        {
            if (File.Exists(CredentialsPath)) File.Delete(CredentialsPath);
            return;
        }

        var output = string.Join(Environment.NewLine, existing) + Environment.NewLine;
        AtomicWrite(CredentialsPath, output);
    }

    private static string? CredentialReference(ModelSettings settings)
    {
        if (settings.ProviderId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            return DeepSeekCredentialRef;

        return settings.ProviderId is "openai-compatible" or "ollama" or "lmstudio"
            ? CompatibleCredentialRef
            : null;
    }

    private static bool IsCredentialLine(string line, string reference)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(reference + ":", StringComparison.Ordinal);
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string ParseYamlScalar(string raw)
    {
        if (raw.StartsWith('"') && raw.EndsWith('"'))
        {
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        var comment = raw.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? raw[..comment] : raw).Trim();
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
        }

        return string.Join(Environment.NewLine, kept).TrimEnd();
    }
}
