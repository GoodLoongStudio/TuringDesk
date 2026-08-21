#include "turingdesk/L3Agent.h"
#include <wincred.h>
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string_view>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kCredentialTarget[] = L"TuringDesk/ModelApiKey";
constexpr std::size_t kMaxConversationTurns = 6;

std::wstring Trim(std::wstring value) {
    const auto notSpace = [](wchar_t ch) { return !std::iswspace(ch); };
    value.erase(value.begin(), std::find_if(value.begin(), value.end(), notSpace));
    value.erase(std::find_if(value.rbegin(), value.rend(), notSpace).base(), value.end());
    return value;
}

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

bool EndsWithInsensitive(const std::wstring& value, const std::wstring& suffix) {
    if (suffix.size() > value.size()) return false;
    return Lower(value.substr(value.size() - suffix.size())) == Lower(suffix);
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string out(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring out(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count);
    return out;
}

std::string EscapeJson(const std::wstring& value) {
    const auto utf8 = WideToUtf8(value);
    std::string out;
    out.reserve(utf8.size() + 16);
    for (unsigned char ch : utf8) {
        switch (ch) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (ch < 0x20) {
                char buffer[7]{};
                sprintf_s(buffer, "\\u%04x", static_cast<unsigned>(ch));
                out += buffer;
            } else {
                out.push_back(static_cast<char>(ch));
            }
        }
    }
    return out;
}

int Hex(char ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

void AppendCodepoint(std::string& out, unsigned cp) {
    if (cp <= 0x7f) out.push_back(static_cast<char>(cp));
    else if (cp <= 0x7ff) {
        out.push_back(static_cast<char>(0xc0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3f)));
    } else if (cp <= 0xffff) {
        out.push_back(static_cast<char>(0xe0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3f)));
    } else {
        out.push_back(static_cast<char>(0xf0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3f)));
    }
}

bool ReadHex4(std::string_view text, std::size_t pos, unsigned& cp) {
    if (pos + 4 > text.size()) return false;
    cp = 0;
    for (std::size_t i = 0; i < 4; ++i) {
        const int value = Hex(text[pos + i]);
        if (value < 0) return false;
        cp = (cp << 4) | static_cast<unsigned>(value);
    }
    return true;
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
        if (ch != '\\') { out.push_back(ch); continue; }
        if (pos >= json.size()) break;
        const char esc = json[pos++];
        switch (esc) {
        case '"': out.push_back('"'); break;
        case '\\': out.push_back('\\'); break;
        case '/': out.push_back('/'); break;
        case 'b': out.push_back('\b'); break;
        case 'f': out.push_back('\f'); break;
        case 'n': out.push_back('\n'); break;
        case 'r': out.push_back('\r'); break;
        case 't': out.push_back('\t'); break;
        case 'u': {
            unsigned cp = 0;
            if (!ReadHex4(json, pos, cp)) return out;
            pos += 4;
            if (cp >= 0xd800 && cp <= 0xdbff && pos + 6 <= json.size() && json[pos] == '\\' && json[pos + 1] == 'u') {
                unsigned low = 0;
                if (ReadHex4(json, pos + 2, low) && low >= 0xdc00 && low <= 0xdfff) {
                    cp = 0x10000 + ((cp - 0xd800) << 10) + (low - 0xdc00);
                    pos += 6;
                }
            }
            AppendCodepoint(out, cp);
            break;
        }
        default: out.push_back(esc); break;
        }
    }
    return out;
}

std::vector<std::wstring> ExtractModelIds(const std::string& json) {
    std::vector<std::wstring> models;
    std::size_t pos = 0;
    constexpr std::string_view key = "\"id\"";
    while ((pos = json.find(key, pos)) != std::string::npos) {
        const auto value = ExtractJsonString(std::string_view(json).substr(pos), key);
        if (!value.empty()) {
            auto wide = Utf8ToWide(value);
            if (wide.starts_with(L"models/")) wide.erase(0, 7);
            if (!wide.empty() && std::find(models.begin(), models.end(), wide) == models.end()) {
                models.push_back(std::move(wide));
            }
        }
        pos += key.size();
    }
    return models;
}

fs::path SettingsPath() {
    wchar_t localAppData[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
    fs::path directory = count ? fs::path(localAppData) / L"TuringDesk" : fs::temp_directory_path() / L"TuringDesk";
    std::error_code ec;
    fs::create_directories(directory, ec);
    return directory / L"model-settings.json";
}

std::wstring ConfigValue(const std::string& json, std::string_view key) {
    return Utf8ToWide(ExtractJsonString(json, key));
}

std::wstring HttpError(DWORD error) {
    return L"WinHTTP 错误 " + std::to_wstring(error);
}

std::wstring NormalizePrefix(std::wstring prefix) {
    prefix = Trim(std::move(prefix));
    if (prefix.empty() || prefix == L"/") return {};
    if (prefix.front() != L'/') prefix.insert(prefix.begin(), L'/');
    while (prefix.size() > 1 && prefix.back() == L'/') prefix.pop_back();
    return prefix;
}

std::wstring JoinPath(const std::wstring& prefix, const wchar_t* suffix) {
    const auto normalized = NormalizePrefix(prefix);
    return normalized.empty() ? std::wstring(suffix) : normalized + suffix;
}

std::wstring BuildRequestPath(const std::wstring& basePathRaw, const std::wstring& endpointRaw) {
    std::wstring basePath = NormalizePrefix(basePathRaw);
    std::wstring endpoint = Trim(endpointRaw);
    if (endpoint == L"-") endpoint.clear();
    if (endpoint.empty()) return basePath.empty() ? L"/" : basePath;
    if (endpoint.front() != L'/') endpoint.insert(endpoint.begin(), L'/');
    if (!basePath.empty() && endpoint.starts_with(basePath)) return endpoint;
    return basePath.empty() ? endpoint : basePath + endpoint;
}

struct ParsedUrl {
    bool valid{};
    std::wstring root;
    std::wstring host;
    std::wstring path;
};

ParsedUrl ParseUrl(const std::wstring& raw) {
    ParsedUrl out;
    const auto value = Trim(raw);
    if (!value.starts_with(L"https://") && !value.starts_with(L"http://")) return out;

    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwSchemeLength = static_cast<DWORD>(-1);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(value.c_str(), 0, 0, &parts) || !parts.lpszHostName || parts.dwHostNameLength == 0) return out;

    out.host.assign(parts.lpszHostName, parts.dwHostNameLength);
    if (parts.lpszUrlPath && parts.dwUrlPathLength) out.path.assign(parts.lpszUrlPath, parts.dwUrlPathLength);
    if (out.path == L"/") out.path.clear();
    while (out.path.size() > 1 && out.path.back() == L'/') out.path.pop_back();

    std::wstring authorityHost = out.host;
    if (authorityHost.find(L':') != std::wstring::npos && !authorityHost.starts_with(L"[")) {
        authorityHost = L"[" + authorityHost + L"]";
    }
    const bool secure = parts.nScheme == INTERNET_SCHEME_HTTPS;
    out.root = secure ? L"https://" : L"http://";
    out.root += authorityHost;
    const bool defaultPort = (secure && parts.nPort == 443) || (!secure && parts.nPort == 80);
    if (!defaultPort && parts.nPort != 0) out.root += L":" + std::to_wstring(parts.nPort);
    out.valid = true;
    return out;
}

bool IsLocalHost(const std::wstring& host) {
    const auto lower = Lower(host);
    return lower == L"localhost" || lower == L"127.0.0.1" || lower == L"::1";
}

bool IsLocalUrl(const std::wstring& url) {
    const auto parsed = ParseUrl(url);
    return parsed.valid && IsLocalHost(parsed.host);
}

struct ProviderCandidate {
    std::wstring providerId;
    std::wstring protocolLabel;
    std::wstring baseUrl;
    std::wstring chatPath;
    std::wstring modelsPath;
    bool anthropic{};
};

std::wstring PrefixFromPath(const std::wstring& path, bool anthropic) {
    if (path.empty()) return {};
    const std::wstring chatSuffix = anthropic ? L"/messages" : L"/chat/completions";
    if (EndsWithInsensitive(path, chatSuffix)) return NormalizePrefix(path.substr(0, path.size() - chatSuffix.size()));
    if (EndsWithInsensitive(path, L"/models")) return NormalizePrefix(path.substr(0, path.size() - 7));
    return NormalizePrefix(path);
}

ProviderCandidate MakeCandidate(const ParsedUrl& parsed,
                                std::wstring providerId,
                                std::wstring label,
                                std::wstring prefix,
                                bool anthropic = false) {
    ProviderCandidate candidate;
    candidate.providerId = std::move(providerId);
    candidate.protocolLabel = std::move(label);
    candidate.baseUrl = parsed.root;
    candidate.anthropic = anthropic;
    candidate.chatPath = JoinPath(prefix, anthropic ? L"/messages" : L"/chat/completions");
    candidate.modelsPath = JoinPath(prefix, L"/models");
    return candidate;
}

std::vector<ProviderCandidate> BuildProviderCandidates(const std::wstring& apiUrl) {
    const auto parsed = ParseUrl(apiUrl);
    if (!parsed.valid) return {};

    const auto host = Lower(parsed.host);
    const auto path = Lower(parsed.path);
    std::vector<ProviderCandidate> candidates;

    const bool looksAnthropic = host.find(L"anthropic.com") != std::wstring::npos || EndsWithInsensitive(path, L"/messages");
    if (looksAnthropic) {
        auto prefix = parsed.path.empty() ? L"/v1" : PrefixFromPath(parsed.path, true);
        candidates.push_back(MakeCandidate(parsed, L"anthropic", L"Anthropic Messages", prefix, true));
        return candidates;
    }

    if (host.find(L"generativelanguage.googleapis.com") != std::wstring::npos) {
        auto prefix = parsed.path.empty() ? L"/v1beta/openai" : PrefixFromPath(parsed.path, false);
        candidates.push_back(MakeCandidate(parsed, L"gemini", L"Gemini · OpenAI-compatible", prefix));
        return candidates;
    }

    if (host.find(L"openrouter.ai") != std::wstring::npos) {
        auto prefix = parsed.path.empty() ? L"/api/v1" : PrefixFromPath(parsed.path, false);
        candidates.push_back(MakeCandidate(parsed, L"openrouter", L"OpenRouter · OpenAI-compatible", prefix));
        return candidates;
    }

    if (host.find(L"api.openai.com") != std::wstring::npos) {
        auto prefix = parsed.path.empty() ? L"/v1" : PrefixFromPath(parsed.path, false);
        candidates.push_back(MakeCandidate(parsed, L"openai", L"OpenAI", prefix));
        return candidates;
    }

    if (host.find(L"deepseek.com") != std::wstring::npos) {
        auto prefix = parsed.path.empty() ? L"" : PrefixFromPath(parsed.path, false);
        candidates.push_back(MakeCandidate(parsed, L"deepseek", L"DeepSeek · OpenAI-compatible", prefix));
        if (parsed.path.empty()) candidates.push_back(MakeCandidate(parsed, L"deepseek", L"DeepSeek · OpenAI-compatible", L"/v1"));
        return candidates;
    }

    if (IsLocalHost(parsed.host) && (parsed.root.find(L":11434") != std::wstring::npos || host.find(L"ollama") != std::wstring::npos)) {
        auto prefix = parsed.path.empty() ? L"/v1" : PrefixFromPath(parsed.path, false);
        candidates.push_back(MakeCandidate(parsed, L"ollama", L"Ollama · OpenAI-compatible", prefix));
        return candidates;
    }

    if (!parsed.path.empty()) {
        candidates.push_back(MakeCandidate(parsed, IsLocalHost(parsed.host) ? L"local-openai-compatible" : L"openai-compatible",
                                           IsLocalHost(parsed.host) ? L"Local · OpenAI-compatible" : L"OpenAI-compatible",
                                           PrefixFromPath(parsed.path, false)));
        return candidates;
    }

    const auto id = IsLocalHost(parsed.host) ? L"local-openai-compatible" : L"openai-compatible";
    const auto label = IsLocalHost(parsed.host) ? L"Local · OpenAI-compatible" : L"OpenAI-compatible";
    candidates.push_back(MakeCandidate(parsed, id, label, L"/v1"));
    candidates.push_back(MakeCandidate(parsed, id, label, L""));
    return candidates;
}

struct HttpResponse {
    bool transportOk{};
    DWORD status{};
    DWORD error{};
    std::string body;
};

HttpResponse HttpGet(const std::wstring& url, const std::wstring& headers) {
    HttpResponse response;
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwSchemeLength = static_cast<DWORD>(-1);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts)) {
        response.error = GetLastError();
        return response;
    }

    const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path = L"/";
    if (parts.lpszUrlPath && parts.dwUrlPathLength) path.assign(parts.lpszUrlPath, parts.dwUrlPathLength);

    HINTERNET session = WinHttpOpen(L"TuringDesk.Native/2.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                    WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) {
        response.error = GetLastError();
        return response;
    }
    WinHttpSetTimeouts(session, 3000, 3000, 5000, 8000);

    HINTERNET connection = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
    if (!connection) {
        response.error = GetLastError();
        WinHttpCloseHandle(session);
        return response;
    }

    const DWORD flags = parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(connection, L"GET", path.c_str(), nullptr, WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!request) {
        response.error = GetLastError();
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return response;
    }

    const LPCWSTR headerPtr = headers.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : headers.c_str();
    const DWORD headerLength = headers.empty() ? 0 : static_cast<DWORD>(-1L);
    if (!WinHttpSendRequest(request, headerPtr, headerLength, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(request, nullptr)) {
        response.error = GetLastError();
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return response;
    }

    DWORD statusBytes = sizeof(response.status);
    WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                        &response.status, &statusBytes, WINHTTP_NO_HEADER_INDEX);

    for (;;) {
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available)) {
            response.error = GetLastError();
            break;
        }
        if (available == 0) {
            response.transportOk = true;
            break;
        }
        std::vector<char> buffer(available);
        DWORD read = 0;
        if (!WinHttpReadData(request, buffer.data(), available, &read)) {
            response.error = GetLastError();
            break;
        }
        if (read == 0) {
            response.transportOk = true;
            break;
        }
        if (response.body.size() + read <= 2 * 1024 * 1024) response.body.append(buffer.data(), read);
    }

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return response;
}

std::wstring ProbeHeaders(const ProviderCandidate& candidate, const std::wstring& apiKey) {
    std::wstring headers = L"Accept: application/json\r\n";
    if (candidate.anthropic) {
        if (!apiKey.empty()) headers += L"x-api-key: " + apiKey + L"\r\n";
        headers += L"anthropic-version: 2023-06-01\r\n";
    } else if (!apiKey.empty()) {
        headers += L"Authorization: Bearer " + apiKey + L"\r\n";
    }
    return headers;
}

int ModelScore(const std::wstring& model, const std::wstring& current, const std::wstring& providerId) {
    const auto lower = Lower(model);
    int score = 0;
    if (!current.empty() && Lower(current) == lower) score += 1000;
    if (providerId == L"deepseek" && lower == L"deepseek-chat") score += 500;
    if (lower.find(L"chat") != std::wstring::npos) score += 120;
    if (lower.find(L"instruct") != std::wstring::npos) score += 90;
    if (lower.find(L"sonnet") != std::wstring::npos) score += 85;
    if (lower.find(L"flash") != std::wstring::npos) score += 80;
    if (lower.find(L"mini") != std::wstring::npos) score += 70;
    if (lower.find(L"reasoner") != std::wstring::npos || lower.find(L"reasoning") != std::wstring::npos) score += 55;
    if (lower.find(L"latest") != std::wstring::npos) score += 20;

    for (const wchar_t* bad : {L"embedding", L"embed", L"rerank", L"moderation", L"whisper", L"tts", L"image", L"audio"}) {
        if (lower.find(bad) != std::wstring::npos) score -= 500;
    }
    return score;
}

std::wstring RecommendModel(const std::vector<std::wstring>& models,
                            const std::wstring& current,
                            const std::wstring& providerId) {
    if (models.empty()) return {};
    std::size_t best = 0;
    int bestScore = ModelScore(models[0], current, providerId);
    for (std::size_t i = 1; i < models.size(); ++i) {
        const int score = ModelScore(models[i], current, providerId);
        if (score > bestScore) {
            best = i;
            bestScore = score;
        }
    }
    return models[best];
}

void FillProbeMetadata(ModelProbeResult& result, const ProviderCandidate& candidate) {
    result.providerId = candidate.providerId;
    result.protocolLabel = candidate.protocolLabel;
    result.baseUrl = candidate.baseUrl;
    result.endpoint = candidate.chatPath;
    result.apiUrl = candidate.baseUrl + candidate.chatPath;
}

} // namespace

L3Agent::L3Agent() : config_(LoadConfig()) {}

L3Agent::~L3Agent() {
    Stop();
    if (worker_.joinable()) worker_.join();
}

ModelConfig L3Agent::LoadConfig() const {
    ModelConfig result;
    std::ifstream stream(SettingsPath(), std::ios::binary);
    if (!stream) return result;
    const std::string json((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    const auto provider = ConfigValue(json, "\"ProviderId\"");
    const auto baseUrl = ConfigValue(json, "\"BaseUrl\"");
    const auto model = ConfigValue(json, "\"Model\"");
    const auto endpoint = ConfigValue(json, "\"Endpoint\"");
    if (!provider.empty()) result.providerId = provider;
    if (!baseUrl.empty()) result.baseUrl = baseUrl;
    if (!model.empty() && model != L"未配置") result.model = model;
    if (!endpoint.empty()) result.endpoint = endpoint;
    return result;
}

bool L3Agent::SaveConfig(const ModelConfig& config) const {
    std::ofstream stream(SettingsPath(), std::ios::binary | std::ios::trunc);
    if (!stream) return false;
    stream << "{\n"
           << "  \"ProviderId\": \"" << EscapeJson(config.providerId) << "\",\n"
           << "  \"Mode\": \"direct\",\n"
           << "  \"BaseUrl\": \"" << EscapeJson(config.baseUrl) << "\",\n"
           << "  \"Model\": \"" << EscapeJson(config.model) << "\",\n"
           << "  \"Endpoint\": \"" << EscapeJson(config.endpoint) << "\",\n"
           << "  \"HasApiKey\": " << (HasStoredApiKey() ? "true" : "false") << "\n}\n";
    return static_cast<bool>(stream);
}

std::wstring L3Agent::LoadApiKey() const {
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

bool L3Agent::SaveApiKey(const std::wstring& key) const {
    if (key.empty()) {
        if (CredDeleteW(kCredentialTarget, CRED_TYPE_GENERIC, 0)) return true;
        return GetLastError() == ERROR_NOT_FOUND;
    }

    CREDENTIALW credential{};
    credential.Type = CRED_TYPE_GENERIC;
    credential.TargetName = const_cast<LPWSTR>(kCredentialTarget);
    credential.CredentialBlobSize = static_cast<DWORD>(key.size() * sizeof(wchar_t));
    credential.CredentialBlob = reinterpret_cast<LPBYTE>(const_cast<wchar_t*>(key.data()));
    credential.Persist = CRED_PERSIST_LOCAL_MACHINE;

    wchar_t user[256]{};
    DWORD userLength = static_cast<DWORD>(std::size(user));
    if (GetUserNameW(user, &userLength)) credential.UserName = user;
    return CredWriteW(&credential, 0) != FALSE;
}

bool L3Agent::HasStoredApiKey() const {
    return !LoadApiKey().empty();
}

bool L3Agent::HasApiKey() const {
    return HasStoredApiKey() || IsLocalUrl(config_.baseUrl);
}

std::wstring L3Agent::CurrentApiUrl() const {
    if (config_.baseUrl.empty()) return {};
    std::wstring endpoint = Trim(config_.endpoint);
    if (endpoint.starts_with(L"/https://") || endpoint.starts_with(L"/http://")) {
        endpoint.erase(endpoint.begin());
        return endpoint;
    }
    if (endpoint.starts_with(L"https://") || endpoint.starts_with(L"http://")) return endpoint;
    std::wstring base = Trim(config_.baseUrl);
    while (base.size() > 1 && base.back() == L'/') base.pop_back();
    if (endpoint.empty() || endpoint == L"-") return base;
    if (endpoint.front() != L'/') endpoint.insert(endpoint.begin(), L'/');
    return base + endpoint;
}

ModelProbeResult L3Agent::ProbeModels(const std::wstring& apiUrl,
                                     const std::wstring& apiKeyOverride,
                                     bool useStoredApiKey) const {
    ModelProbeResult result;
    const auto candidates = BuildProviderCandidates(apiUrl);
    if (candidates.empty()) {
        result.message = L"API 地址无效，请填写以 http:// 或 https:// 开头的地址。";
        return result;
    }

    const std::wstring apiKey = !apiKeyOverride.empty() ? apiKeyOverride : (useStoredApiKey ? LoadApiKey() : L"");
    FillProbeMetadata(result, candidates.front());

    DWORD lastStatus = 0;
    DWORD lastError = ERROR_SUCCESS;
    for (const auto& candidate : candidates) {
        const auto modelsUrl = candidate.baseUrl + candidate.modelsPath;
        const auto response = HttpGet(modelsUrl, ProbeHeaders(candidate, apiKey));
        if (!response.transportOk) {
            lastError = response.error;
            continue;
        }

        lastStatus = response.status;
        FillProbeMetadata(result, candidate);
        result.statusCode = response.status;

        if (response.status == 200) {
            result.models = ExtractModelIds(response.body);
            if (!result.models.empty()) {
                result.ok = true;
                result.recommendedModel = RecommendModel(result.models, config_.model, result.providerId);
                result.message = L"已自动识别协议并读取模型列表。";
                return result;
            }
            result.message = L"连接成功，但服务没有返回可识别的模型 ID。可手动填写模型名称后保存。";
            return result;
        }

        if (response.status == 401 || response.status == 403) {
            result.message = L"API Key 无效、缺失或没有读取模型列表的权限。";
            return result;
        }

        if (response.status != 404 && response.status != 405) {
            std::wstring detail = L"模型列表请求返回 HTTP " + std::to_wstring(response.status) + L"。";
            if (!response.body.empty()) {
                const auto shortBody = Utf8ToWide(response.body.substr(0, 220));
                if (!shortBody.empty()) detail += L" " + shortBody;
            }
            result.message = std::move(detail);
            return result;
        }
    }

    result.statusCode = lastStatus;
    if (lastError != ERROR_SUCCESS) {
        result.message = L"无法连接 API：" + HttpError(lastError);
    } else {
        result.message = L"已推断为 " + result.protocolLabel + L"，但没有找到模型列表接口。可手动填写模型名称后保存。";
    }
    return result;
}

bool L3Agent::ApplyModelConfig(const ModelProbeResult& probe,
                               const std::wstring& rawModel,
                               const std::wstring& apiKeyOverride,
                               bool preserveExistingKey,
                               std::wstring& reply) {
    const auto model = Trim(rawModel);
    if (probe.baseUrl.empty() || probe.endpoint.empty() || model.empty()) {
        reply = L"API 地址或模型名称为空。";
        return false;
    }

    if (!apiKeyOverride.empty()) {
        if (!SaveApiKey(apiKeyOverride)) {
            reply = L"API Key 保存失败，Windows 错误：" + std::to_wstring(GetLastError());
            return false;
        }
    } else if (!preserveExistingKey && !IsLocalUrl(probe.baseUrl)) {
        if (!HasStoredApiKey()) {
            reply = L"该远程服务需要 API Key。";
            return false;
        }
    }

    const bool changed = config_.providerId != probe.providerId || config_.baseUrl != probe.baseUrl ||
                         config_.endpoint != probe.endpoint || config_.model != model;
    config_.providerId = probe.providerId.empty() ? L"openai-compatible" : probe.providerId;
    config_.baseUrl = probe.baseUrl;
    config_.endpoint = probe.endpoint;
    config_.model = model;
    if (changed) ClearConversation();

    if (!SaveConfig(config_)) {
        reply = L"模型配置保存失败。";
        return false;
    }
    reply = L"已保存 · " + (probe.protocolLabel.empty() ? config_.providerId : probe.protocolLabel) + L" · " + config_.model;
    return true;
}

void L3Agent::ClearConversation() {
    std::scoped_lock lock(conversationMutex_);
    conversation_.clear();
}

bool L3Agent::TryHandleLocal(const std::wstring& raw, std::wstring& reply, bool& consumedSecret) {
    consumedSecret = false;
    const auto input = Trim(raw);
    const auto lower = Lower(input);

    if (lower == L"/help") {
        reply = L"L3 命令：/status、/time、/new。模型和 API Key 请使用右上角 AI 设置；Ctrl+Enter 强制进入 L3。";
        return true;
    }
    if (lower == L"/status") {
        reply = L"Native L3 · Provider=" + config_.providerId + L" · Model=" +
                (config_.model.empty() ? L"未配置" : config_.model) + L" · API Key=" +
                (HasStoredApiKey() ? L"已配置" : (IsLocalUrl(config_.baseUrl) ? L"本地服务无需 Key" : L"未配置")) +
                L" · Harness=未参与";
        return true;
    }
    if (lower == L"/new" || lower == L"/new-chat" || lower == L"新对话") {
        Stop();
        if (worker_.joinable()) worker_.join();
        ClearConversation();
        reply = L"已开始新的 L3 对话。";
        return true;
    }
    if (lower == L"/time" || lower == L"现在几点" || lower == L"现在几点？" || lower == L"当前时间") {
        SYSTEMTIME now{};
        GetLocalTime(&now);
        wchar_t buffer[64]{};
        swprintf_s(buffer, L"当前时间：%04u-%02u-%02u %02u:%02u:%02u", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
        reply = buffer;
        return true;
    }
    if (lower.starts_with(L"/key ")) {
        consumedSecret = true;
        const auto key = Trim(input.substr(5));
        if (key.empty()) reply = L"API Key 不能为空。";
        else if (SaveApiKey(key)) {
            SaveConfig(config_);
            reply = L"API Key 已保存到 Windows Credential Manager，搜索框明文已清除。";
        } else {
            reply = L"API Key 保存失败，Windows 错误：" + std::to_wstring(GetLastError());
        }
        return true;
    }
    if (lower == L"/clear-key") {
        const bool ok = SaveApiKey(L"");
        SaveConfig(config_);
        reply = ok ? L"API Key 已删除。" : L"API Key 删除失败。";
        return true;
    }
    if (lower.starts_with(L"/endpoint ")) {
        auto endpoint = Trim(input.substr(10));
        if (endpoint == L"-") endpoint.clear();
        if (!endpoint.empty() && endpoint.front() != L'/') endpoint.insert(endpoint.begin(), L'/');
        const bool changed = config_.endpoint != endpoint;
        config_.endpoint = endpoint;
        if (changed) ClearConversation();
        reply = SaveConfig(config_) ? L"Endpoint 已保存。" : L"Endpoint 保存失败。";
        return true;
    }
    if (lower.starts_with(L"/provider ")) {
        const auto args = Trim(input.substr(10));
        const auto split = args.find_first_of(L" \t");
        if (split == std::wstring::npos) {
            reply = L"用法：/provider <BaseURL> <Model>";
            return true;
        }
        const auto baseUrl = Trim(args.substr(0, split));
        const auto model = Trim(args.substr(split + 1));
        if ((!baseUrl.starts_with(L"https://") && !baseUrl.starts_with(L"http://")) || model.empty()) {
            reply = L"Base URL 必须以 http:// 或 https:// 开头，并填写模型 ID。";
            return true;
        }
        const auto lowerBase = Lower(baseUrl);
        const bool changed = config_.baseUrl != baseUrl || config_.model != model;
        config_.baseUrl = baseUrl;
        config_.model = model;
        if (lowerBase.find(L"anthropic") != std::wstring::npos) config_.providerId = L"anthropic";
        else if (lowerBase.find(L"deepseek") != std::wstring::npos) config_.providerId = L"deepseek";
        else config_.providerId = L"openai-compatible";
        if (changed) ClearConversation();
        reply = SaveConfig(config_) ? L"模型配置已保存。" : L"模型配置保存失败。";
        return true;
    }
    return false;
}

void L3Agent::Stop() {
    if (worker_.joinable()) worker_.request_stop();
    if (auto request = activeRequest_.exchange(nullptr)) WinHttpCloseHandle(request);
}

void L3Agent::AskAsync(std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone) {
    Stop();
    if (worker_.joinable()) worker_.join();
    busy_.store(true);
    worker_ = std::jthread([this, prompt = std::move(prompt), onDelta = std::move(onDelta), onDone = std::move(onDone)](std::stop_token token) mutable {
        RunRequest(std::move(prompt), std::move(onDelta), std::move(onDone), token);
        busy_.store(false);
    });
}

void L3Agent::RunRequest(std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone, std::stop_token stopToken) {
    const auto apiKey = LoadApiKey();
    if (apiKey.empty() && !IsLocalUrl(config_.baseUrl)) {
        onDone(L"未配置 API Key。请打开右上角 AI 设置填写 Key。");
        return;
    }
    if (config_.baseUrl.empty() || config_.endpoint.empty() || config_.model.empty()) {
        onDone(L"L3 模型尚未配置。请打开右上角 AI 设置。");
        return;
    }

    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwSchemeLength = static_cast<DWORD>(-1);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(config_.baseUrl.c_str(), 0, 0, &parts)) {
        onDone(L"模型 API 地址无效。请在 AI 设置中重新检测。");
        return;
    }

    const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring basePath;
    if (parts.lpszUrlPath && parts.dwUrlPathLength) basePath.assign(parts.lpszUrlPath, parts.dwUrlPathLength);
    const std::wstring path = BuildRequestPath(basePath, config_.endpoint);

    HINTERNET session = WinHttpOpen(L"TuringDesk.Native/2.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                    WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) { onDone(HttpError(GetLastError())); return; }
    WinHttpSetTimeouts(session, 5000, 5000, 15000, 60000);

    HINTERNET connection = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
    if (!connection) {
        const auto error = GetLastError();
        WinHttpCloseHandle(session);
        onDone(HttpError(error));
        return;
    }

    const DWORD requestFlags = parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(connection, L"POST", path.c_str(), nullptr, WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES, requestFlags);
    if (!request) {
        const auto error = GetLastError();
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        onDone(HttpError(error));
        return;
    }
    activeRequest_.store(request);

    std::vector<ChatTurn> history;
    {
        std::scoped_lock lock(conversationMutex_);
        history = conversation_;
    }

    const bool anthropic = config_.providerId == L"anthropic";
    std::wstring headers = L"Content-Type: application/json\r\nAccept: text/event-stream\r\n";
    if (anthropic) {
        headers += L"x-api-key: " + apiKey + L"\r\nanthropic-version: 2023-06-01\r\n";
    } else if (!apiKey.empty()) {
        headers += L"Authorization: Bearer " + apiKey + L"\r\n";
    }

    std::string body;
    if (anthropic) {
        body = "{\"model\":\"" + EscapeJson(config_.model) +
               "\",\"max_tokens\":1024,\"stream\":true,\"system\":\"You are TuringDesk L3. Be concise. Never claim an OS action ran unless a registered native tool actually ran it.\",\"messages\":[";
        bool first = true;
        for (const auto& turn : history) {
            if (!first) body += ',';
            body += "{\"role\":\"user\",\"content\":\"" + EscapeJson(turn.user) + "\"}";
            body += ",{\"role\":\"assistant\",\"content\":\"" + EscapeJson(turn.assistant) + "\"}";
            first = false;
        }
        if (!first) body += ',';
        body += "{\"role\":\"user\",\"content\":\"" + EscapeJson(prompt) + "\"}]}";
    } else {
        body = "{\"model\":\"" + EscapeJson(config_.model) +
               "\",\"messages\":[{\"role\":\"system\",\"content\":\"You are TuringDesk L3. Be concise. Never claim an OS action ran unless a registered native tool actually ran it.\"}";
        for (const auto& turn : history) {
            body += ",{\"role\":\"user\",\"content\":\"" + EscapeJson(turn.user) + "\"}";
            body += ",{\"role\":\"assistant\",\"content\":\"" + EscapeJson(turn.assistant) + "\"}";
        }
        body += ",{\"role\":\"user\",\"content\":\"" + EscapeJson(prompt) + "\"}],\"stream\":true";
        if (Lower(host).find(L"deepseek") != std::wstring::npos) body += ",\"thinking\":{\"type\":\"disabled\"}";
        body += "}";
    }

    auto closeHandles = [&]() {
        if (activeRequest_.exchange(nullptr) == request) WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
    };

    if (!WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(-1L), body.data(), static_cast<DWORD>(body.size()),
                            static_cast<DWORD>(body.size()), 0) ||
        !WinHttpReceiveResponse(request, nullptr)) {
        const auto error = GetLastError();
        closeHandles();
        onDone(stopToken.stop_requested() ? L"已停止" : HttpError(error));
        return;
    }

    DWORD status = 0;
    DWORD statusBytes = sizeof(status);
    WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX,
                        &status, &statusBytes, WINHTTP_NO_HEADER_INDEX);

    std::string pending;
    std::string fullBody;
    std::wstring assistantText;
    bool emitted = false;
    bool done = false;
    DWORD streamError = ERROR_SUCCESS;
    while (!stopToken.stop_requested() && !done) {
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available)) {
            streamError = GetLastError();
            break;
        }
        if (available == 0) break;
        std::vector<char> buffer(available);
        DWORD read = 0;
        if (!WinHttpReadData(request, buffer.data(), available, &read)) {
            streamError = GetLastError();
            break;
        }
        if (read == 0) break;
        pending.append(buffer.data(), read);
        fullBody.append(buffer.data(), read);

        std::size_t newline = 0;
        while ((newline = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, newline);
            pending.erase(0, newline + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (!line.starts_with("data:")) continue;
            std::string_view data(line);
            data.remove_prefix(5);
            while (!data.empty() && data.front() == ' ') data.remove_prefix(1);
            if (data == "[DONE]") { done = true; break; }
            if (anthropic && data.find("\"type\":\"message_stop\"") != std::string_view::npos) {
                done = true;
                break;
            }

            const auto content = anthropic
                ? ExtractJsonString(data, "\"text\"")
                : ExtractJsonString(data, "\"content\"");
            if (!content.empty()) {
                const auto wide = Utf8ToWide(content);
                if (!wide.empty()) {
                    emitted = true;
                    assistantText += wide;
                    onDelta(wide);
                }
            }
        }
    }

    closeHandles();
    if (stopToken.stop_requested()) { onDone(L"已停止"); return; }
    if (streamError != ERROR_SUCCESS) {
        onDone(L"模型流式传输失败：" + HttpError(streamError) + L"。本次截断内容未写入 L3 会话，可直接重试。");
        return;
    }
    if (status < 200 || status >= 300) {
        std::wstring detail = L"模型请求失败：HTTP " + std::to_wstring(status) + L" · Endpoint=" + path;
        if (!fullBody.empty()) {
            const auto wideBody = Utf8ToWide(fullBody.substr(0, 300));
            if (!wideBody.empty()) detail += L" · " + wideBody;
        }
        onDone(detail);
        return;
    }

    if (!emitted) {
        const auto content = anthropic
            ? ExtractJsonString(fullBody, "\"text\"")
            : ExtractJsonString(fullBody, "\"content\"");
        if (!content.empty()) {
            const auto wide = Utf8ToWide(content);
            if (!wide.empty()) {
                onDelta(wide);
                assistantText = wide;
                emitted = true;
            }
        }
    }

    if (emitted && !assistantText.empty()) {
        std::scoped_lock lock(conversationMutex_);
        conversation_.push_back({std::move(prompt), std::move(assistantText)});
        if (conversation_.size() > kMaxConversationTurns) {
            conversation_.erase(conversation_.begin(), conversation_.begin() + (conversation_.size() - kMaxConversationTurns));
        }
    }
    onDone(emitted ? L"" : L"模型返回成功，但没有可显示的文本内容。请检查服务的兼容格式。");
}

} // namespace turingdesk
