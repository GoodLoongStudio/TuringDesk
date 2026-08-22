#include "turingdesk/HarnessSettingsBridge.h"
#include <wincred.h>
#include <windows.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string_view>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kCredentialTarget[] = L"TuringDesk/ModelApiKey";
constexpr wchar_t kHarnessCredentialEnv[] = L"TURINGDESK_API_KEY";

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring out(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count);
    return out;
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string out(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}

std::string ExtractJsonString(std::string_view json, std::string_view key) {
    auto pos = json.find(key);
    if (pos == std::string_view::npos) return {};
    pos = json.find(':', pos + key.size());
    if (pos == std::string_view::npos) return {};
    pos = json.find('"', pos + 1);
    if (pos == std::string_view::npos) return {};
    ++pos;

    std::string out;
    while (pos < json.size()) {
        const char ch = json[pos++];
        if (ch == '"') break;
        if (ch != '\\') {
            out.push_back(ch);
            continue;
        }
        if (pos >= json.size()) break;
        const char escaped = json[pos++];
        switch (escaped) {
        case '"': out.push_back('"'); break;
        case '\\': out.push_back('\\'); break;
        case '/': out.push_back('/'); break;
        case 'b': out.push_back('\b'); break;
        case 'f': out.push_back('\f'); break;
        case 'n': out.push_back('\n'); break;
        case 'r': out.push_back('\r'); break;
        case 't': out.push_back('\t'); break;
        default: out.push_back(escaped); break;
        }
    }
    return out;
}

fs::path TuringDeskRoot() {
    wchar_t localAppData[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
    if (count == 0 || count >= std::size(localAppData)) return {};
    return fs::path(std::wstring(localAppData, count)) / L"TuringDesk";
}

std::wstring ReadStoredApiKey() {
    PCREDENTIALW credential = nullptr;
    if (!CredReadW(kCredentialTarget, CRED_TYPE_GENERIC, 0, &credential)) return {};
    std::wstring key;
    if (credential && credential->CredentialBlob && credential->CredentialBlobSize > 0) {
        const auto* chars = reinterpret_cast<const wchar_t*>(credential->CredentialBlob);
        key.assign(chars, credential->CredentialBlobSize / sizeof(wchar_t));
    }
    if (credential) CredFree(credential);
    return key;
}

std::wstring JoinApiUrl(const std::wstring& baseUrl, const std::wstring& endpoint) {
    if (endpoint.starts_with(L"https://") || endpoint.starts_with(L"http://")) return endpoint;
    std::wstring base = baseUrl;
    while (base.size() > 1 && base.back() == L'/') base.pop_back();
    if (endpoint.empty() || endpoint == L"-") return base;
    std::wstring suffix = endpoint;
    if (suffix.front() != L'/') suffix.insert(suffix.begin(), L'/');
    return base + suffix;
}

bool EndsWithInsensitive(const std::wstring& value, const std::wstring& suffix) {
    if (suffix.size() > value.size()) return false;
    const auto offset = value.size() - suffix.size();
    for (std::size_t i = 0; i < suffix.size(); ++i) {
        if (std::towlower(value[offset + i]) != std::towlower(suffix[i])) return false;
    }
    return true;
}

std::wstring HarnessBaseUrl(const std::wstring& baseUrl, const std::wstring& endpoint, bool anthropic) {
    std::wstring full = JoinApiUrl(baseUrl, endpoint);
    const std::wstring suffix = anthropic ? L"/messages" : L"/chat/completions";
    if (EndsWithInsensitive(full, suffix)) full.erase(full.size() - suffix.size());
    while (full.size() > 1 && full.back() == L'/') full.pop_back();
    return full;
}

std::string YamlQuote(const std::wstring& value) {
    const std::string utf8 = WideToUtf8(value);
    std::string out = "'";
    for (char ch : utf8) {
        if (ch == '\'') out += "''";
        else out.push_back(ch);
    }
    out.push_back('\'');
    return out;
}

std::string BuildSettingsYaml(const std::wstring& providerId,
                              const std::wstring& baseUrl,
                              const std::wstring& endpoint,
                              const std::wstring& model,
                              bool hasApiKey) {
    const bool anthropic = providerId == L"anthropic";
    const std::wstring protocol = anthropic ? L"anthropic-messages" : L"openai-completions";
    const std::wstring resolvedBase = HarnessBaseUrl(baseUrl, endpoint, anthropic);

    std::string yaml;
    yaml += "# Managed by TuringDesk. Provider/Base URL/Model are synchronized from model-settings.json.\n";
    yaml += "# API key stays in Windows Credential Manager and is injected only into the DSH child process.\n";
    yaml += "llm-pi-ai:\n";
    yaml += "  providers:\n";
    yaml += "    turingdesk:\n";
    yaml += "      displayName: 'TuringDesk'\n";
    if (hasApiKey) yaml += "      apiKeyEnv: TURINGDESK_API_KEY\n";
    yaml += "      api: " + YamlQuote(protocol) + "\n";
    yaml += "      baseURL: " + YamlQuote(resolvedBase) + "\n";
    yaml += "      models:\n";
    yaml += "        - id: " + YamlQuote(model) + "\n";
    yaml += "agent-default-model:\n";
    yaml += "  provider: turingdesk\n";
    yaml += "  model: " + YamlQuote(model) + "\n";
    return yaml;
}

bool WriteAtomically(const fs::path& path, const std::string& content) {
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);
    if (ec) return false;

    fs::path temp = path;
    temp += L".tmp";
    {
        std::ofstream stream(temp, std::ios::binary | std::ios::trunc);
        if (!stream) return false;
        stream.write(content.data(), static_cast<std::streamsize>(content.size()));
        stream.flush();
        if (!stream) {
            stream.close();
            fs::remove(temp, ec);
            return false;
        }
    }

    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        fs::remove(temp, ec);
        return false;
    }
    return true;
}

} // namespace

HarnessSettingsBridgeState PrepareHarnessSettingsBridge() {
    HarnessSettingsBridgeState state;
    const fs::path root = TuringDeskRoot();
    if (root.empty()) {
        state.error = L"LOCALAPPDATA 不可用";
        return state;
    }

    const fs::path dshHome = root / L"Harness" / L"DshHome";
    state.dshHome = dshHome.wstring();
    std::error_code ec;
    fs::create_directories(dshHome, ec);
    if (ec) {
        state.error = L"无法创建 TuringDesk Harness DSH_HOME";
        return state;
    }

    const fs::path settingsPath = root / L"model-settings.json";
    std::ifstream stream(settingsPath, std::ios::binary);
    if (!stream) return state; // Harness may still run with its own defaults when TuringDesk is unconfigured.

    const std::string json((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    state.providerId = Utf8ToWide(ExtractJsonString(json, "\"ProviderId\""));
    const std::wstring baseUrl = Utf8ToWide(ExtractJsonString(json, "\"BaseUrl\""));
    state.model = Utf8ToWide(ExtractJsonString(json, "\"Model\""));
    const std::wstring endpoint = Utf8ToWide(ExtractJsonString(json, "\"Endpoint\""));
    if (baseUrl.empty() || state.model.empty() || state.model == L"未配置") return state;

    if (state.providerId.empty() || state.providerId == L"unconfigured") state.providerId = L"openai-compatible";
    const bool anthropic = state.providerId == L"anthropic";
    state.protocol = anthropic ? L"anthropic-messages" : L"openai-completions";
    state.baseUrl = HarnessBaseUrl(baseUrl, endpoint, anthropic);
    if (state.baseUrl.empty()) {
        state.error = L"TuringDesk 模型 Base URL 无法转换为 Harness Provider URL";
        return state;
    }

    state.apiKey = ReadStoredApiKey();
    state.hasApiKey = !state.apiKey.empty();

    const std::string yaml = BuildSettingsYaml(state.providerId, baseUrl, endpoint, state.model, state.hasApiKey);
    if (!WriteAtomically(dshHome / L"settings.yaml", yaml)) {
        state.error = L"无法写入 TuringDesk Harness settings.yaml";
        state.apiKey.clear();
        return state;
    }

    state.configured = true;
    return state;
}

bool HarnessSettingsBridgeSelfTest() {
    const std::string openAi = BuildSettingsYaml(L"openai-compatible",
                                                  L"https://gateway.example",
                                                  L"/v1/chat/completions",
                                                  L"demo/model",
                                                  true);
    const std::string anthropic = BuildSettingsYaml(L"anthropic",
                                                     L"https://api.anthropic.com",
                                                     L"/v1/messages",
                                                     L"claude-test",
                                                     true);
    return openAi.find("provider: turingdesk") != std::string::npos &&
           openAi.find("model: 'demo/model'") != std::string::npos &&
           openAi.find("baseURL: 'https://gateway.example/v1'") != std::string::npos &&
           openAi.find("apiKeyEnv: TURINGDESK_API_KEY") != std::string::npos &&
           openAi.find("sk-") == std::string::npos &&
           anthropic.find("api: 'anthropic-messages'") != std::string::npos &&
           anthropic.find("baseURL: 'https://api.anthropic.com/v1'") != std::string::npos &&
           std::wstring(kHarnessCredentialEnv) == L"TURINGDESK_API_KEY";
}

} // namespace turingdesk
