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

std::wstring BuildRequestPath(const std::wstring& basePathRaw, const std::wstring& endpointRaw) {
    std::wstring basePath = basePathRaw;
    if (basePath.empty()) basePath = L"/";
    while (basePath.size() > 1 && basePath.back() == L'/') basePath.pop_back();

    std::wstring endpoint = Trim(endpointRaw);
    if (endpoint == L"-") endpoint.clear();
    if (endpoint.empty()) return basePath.empty() ? L"/" : basePath;
    if (endpoint.front() != L'/') endpoint.insert(endpoint.begin(), L'/');

    if (basePath == L"/") basePath.clear();
    if (!basePath.ends_with(endpoint)) basePath += endpoint;
    return basePath.empty() ? L"/" : basePath;
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
    if (!provider.empty() && provider != L"unconfigured") result.providerId = provider;
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
           << "  \"HasApiKey\": " << (HasApiKey() ? "true" : "false") << "\n}\n";
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

bool L3Agent::HasApiKey() const {
    return !LoadApiKey().empty();
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
        reply = L"L3 命令：/status、/time、/new、/provider <BaseURL> <Model>、/endpoint <Path>、/key <API Key>、/clear-key。Ctrl+Enter 强制使用模型。";
        return true;
    }
    if (lower == L"/status") {
        reply = L"Native L3 · Provider=" + config_.providerId + L" · Model=" + config_.model +
                L" · Endpoint=" + (config_.endpoint.empty() ? L"(Base URL path)" : config_.endpoint) +
                L" · API Key=" + (HasApiKey() ? L"已配置" : L"未配置") + L" · Harness=未参与";
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
        reply = SaveConfig(config_) ? L"Endpoint 已保存：" + (config_.endpoint.empty() ? L"使用 Base URL 自带路径" : config_.endpoint)
                                    : L"Endpoint 保存失败。";
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
        const bool changed = config_.baseUrl != baseUrl || config_.model != model;
        config_.baseUrl = baseUrl;
        config_.model = model;
        config_.providerId = Lower(baseUrl).find(L"deepseek") != std::wstring::npos ? L"deepseek" : L"openai-compatible";
        if (changed) ClearConversation();
        reply = SaveConfig(config_) ? L"模型配置已保存：" + config_.model + L" @ " + config_.baseUrl : L"模型配置保存失败。";
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
    if (apiKey.empty()) {
        onDone(L"未配置 API Key。请打开右上角 AI 设置填写 Key。");
        return;
    }

    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwSchemeLength = static_cast<DWORD>(-1);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(config_.baseUrl.c_str(), 0, 0, &parts)) {
        onDone(L"模型 Base URL 无效。请在 AI 设置中重新填写。");
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

    const std::wstring headers = L"Content-Type: application/json\r\nAccept: text/event-stream\r\nAuthorization: Bearer " + apiKey + L"\r\n";
    std::string body = "{\"model\":\"" + EscapeJson(config_.model) +
                       "\",\"messages\":[{\"role\":\"system\",\"content\":\"You are TuringDesk L3. Be concise. Never claim an OS action ran unless a registered native tool actually ran it.\"}";
    for (const auto& turn : history) {
        body += ",{\"role\":\"user\",\"content\":\"" + EscapeJson(turn.user) + "\"}";
        body += ",{\"role\":\"assistant\",\"content\":\"" + EscapeJson(turn.assistant) + "\"}";
    }
    body += ",{\"role\":\"user\",\"content\":\"" + EscapeJson(prompt) + "\"}],\"stream\":true";
    if (Lower(host).find(L"deepseek") != std::wstring::npos) body += ",\"thinking\":{\"type\":\"disabled\"}";
    body += "}";

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
            const auto content = ExtractJsonString(data, "\"content\"");
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
            std::string shortBody = fullBody.substr(0, 300);
            const auto wideBody = Utf8ToWide(shortBody);
            if (!wideBody.empty()) detail += L" · " + wideBody;
        }
        onDone(detail);
        return;
    }

    if (!emitted) {
        const auto content = ExtractJsonString(fullBody, "\"content\"");
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
    onDone(emitted ? L"" : L"模型返回成功，但没有可显示的 content。请检查 OpenAI-compatible 响应格式。");
}

} // namespace turingdesk
