#include "turingdesk/L3Agent.h"
#include <winhttp.h>
#include <wincred.h>
#include <windows.h>
#include <algorithm>
#include <chrono>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string_view>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kCredentialTarget[] = L"TuringDesk/ModelApiKey";

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
    std::string out(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring out(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), out.data(), count);
    return out;
}

std::string EscapeJsonUtf8(const std::wstring& value) {
    const auto utf8 = WideToUtf8(value);
    std::string out;
    out.reserve(utf8.size() + 16);
    for (unsigned char ch : utf8) {
        switch (ch) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (ch < 0x20) {
                char buffer[7];
                sprintf_s(buffer, "\\u%04x", ch);
                out += buffer;
            } else {
                out.push_back(static_cast<char>(ch));
            }
        }
    }
    return out;
}

void AppendUtf8Codepoint(std::string& out, unsigned codepoint) {
    if (codepoint <= 0x7f) out.push_back(static_cast<char>(codepoint));
    else if (codepoint <= 0x7ff) {
        out.push_back(static_cast<char>(0xc0 | (codepoint >> 6)));
        out.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
    } else if (codepoint <= 0xffff) {
        out.push_back(static_cast<char>(0xe0 | (codepoint >> 12)));
        out.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
    } else {
        out.push_back(static_cast<char>(0xf0 | (codepoint >> 18)));
        out.push_back(static_cast<char>(0x80 | ((codepoint >> 12) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
    }
}

int HexValue(char ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

bool ParseHex4(std::string_view text, std::size_t pos, unsigned& value) {
    if (pos + 4 > text.size()) return false;
    value = 0;
    for (std::size_t i = 0; i < 4; ++i) {
        const int h = HexValue(text[pos + i]);
        if (h < 0) return false;
        value = (value << 4) | static_cast<unsigned>(h);
    }
    return true;
}

std::string ExtractJsonString(std::string_view json, std::string_view key) {
    const auto keyPos = json.find(key);
    if (keyPos == std::string_view::npos) return {};
    auto pos = json.find(':', keyPos + key.size());
    if (pos == std::string_view::npos) return {};
    pos = json.find('"', pos + 1);
    if (pos == std::string_view::npos) return {};
    ++pos;

    std::string out;
    while (pos < json.size()) {
        char ch = json[pos++];
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
            if (!ParseHex4(json, pos, cp)) return out;
            pos += 4;
            if (cp >= 0xd800 && cp <= 0xdbff && pos + 6 <= json.size() && json[pos] == '\\' && json[pos + 1] == 'u') {
                unsigned low = 0;
                if (ParseHex4(json, pos + 2, low) && low >= 0xdc00 && low <= 0xdfff) {
                    cp = 0x10000 + ((cp - 0xd800) << 10) + (low - 0xdc00);
                    pos += 6;
                }
            }
            AppendUtf8Codepoint(out, cp);
            break;
        }
        default: out.push_back(esc); break;
        }
    }
    return out;
}

fs::path SettingsPath() {
    wchar_t localAppData[MAX_PATH]{};
    const DWORD len = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
    fs::path dir = len ? fs::path(localAppData) / L"TuringDesk" : fs::temp_directory_path() / L"TuringDesk";
    std::error_code ec;
    fs::create_directories(dir, ec);
    return dir / L"model-settings.json";
}

std::wstring ExtractConfigValue(const std::string& json, std::string_view key) {
    return Utf8ToWide(ExtractJsonString(json, key));
}

std::wstring WinHttpErrorText(DWORD error) {
    return L"WinHTTP 错误 " + std::to_wstring(error);
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
    const auto provider = ExtractConfigValue(json, "\"ProviderId\"");
    const auto baseUrl = ExtractConfigValue(json, "\"BaseUrl\"");
    const auto model = ExtractConfigValue(json, "\"Model\"");
    if (!provider.empty() && provider != L"unconfigured") result.providerId = provider;
    if (!baseUrl.empty()) result.baseUrl = baseUrl;
    if (!model.empty() && model != L"未配置") result.model = model;
    return result;
}

bool L3Agent::SaveConfig(const ModelConfig& config) const {
    std::ofstream stream(SettingsPath(), std::ios::binary | std::ios::trunc);
    if (!stream) return false;
    stream << "{\n"
           << "  \"ProviderId\": \"" << EscapeJsonUtf8(config.providerId) << "\",\n"
           << "  \"Mode\": \"direct\",\n"
           << "  \"BaseUrl\": \"" << EscapeJsonUtf8(config.baseUrl) << "\",\n"
           << "  \"Model\": \"" << EscapeJsonUtf8(config.model) << "\",\n"
           << "  \"HasApiKey\": " << (HasApiKey() ? "true" : "false") << "\n}\n";
    return static_cast<bool>(stream);
}

std::wstring L3Agent::LoadApiKey() const {
    PCREDENTIALW credential = nullptr;
    if (!CredReadW(kCredentialTarget, CRED_TYPE_GENERIC, 0, &credential)) return {};
    std::wstring result;
    if (credential->CredentialBlob && credential->CredentialBlobSize >= sizeof(wchar_t)) {
        const auto* data = reinterpret_cast<const wchar_t*>(credential->CredentialBlob);
        result.assign(data, credential->CredentialBlobSize / sizeof(wchar_t));
    }
    CredFree(credential);
    return result;
}

bool L3Agent::SaveApiKey(const std::wstring& key) const {
    if (key.empty()) return CredDeleteW(kCredentialTarget, CRED_TYPE_GENERIC, 0) != FALSE || GetLastError() == ERROR_NOT_FOUND;
    CREDENTIALW credential{};
    credential.Type = CRED_TYPE_GENERIC;
    credential.TargetName = const_cast<LPWSTR>(kCredentialTarget);
    credential.CredentialBlobSize = static_cast<DWORD>(key.size() * sizeof(wchar_t));
    credential.CredentialBlob = reinterpret_cast<LPBYTE>(const_cast<wchar_t*>(key.data()));
    credential.Persist = CRED_PERSIST_LOCAL_MACHINE;
    wchar_t user[256]{};
    DWORD userLen = static_cast<DWORD>(std::size(user));
    if (GetUserNameW(user, &userLen)) credential.UserName = user;
    return CredWriteW(&credential, 0) != FALSE;
}

bool L3Agent::HasApiKey() const {
    return !LoadApiKey().empty();
}

bool L3Agent::TryHandleLocal(const std::wstring& inputRaw, std::wstring& reply, bool& consumedSecret) {
    consumedSecret = false;
    const auto input = Trim(inputRaw);
    const auto lower = Lower(input);

    if (lower == L"/help") {
        reply = L"L3 本地命令：/status、/time、/provider <BaseURL> <Model>、/key <API Key>、/clear-key。Ctrl+Enter 可强制把当前内容交给模型。";
        return true;
    }
    if (lower == L"/status") {
        reply = L"TuringDesk Native L3 · Provider=" + config_.providerId + L" · Model=" + config_.model +
                L" · API Key=" + (HasApiKey() ? L"已配置" : L"未配置") + L" · Harness=不参与 L3";
        return true;
    }
    if (lower == L"/time" || lower == L"现在几点" || lower == L"现在几点？" || lower == L"当前时间") {
        SYSTEMTIME time{};
        GetLocalTime(&time);
        wchar_t buffer[64]{};
        swprintf_s(buffer, L"当前时间：%04u-%02u-%02u %02u:%02u:%02u", time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);
        reply = buffer;
        return true;
    }
    if (lower.starts_with(L"/key ")) {
        const auto key = Trim(input.substr(5));
        consumedSecret = true;
        if (key.empty()) reply = L"API Key 不能为空。";
        else if (SaveApiKey(key)) {
            SaveConfig(config_);
            reply = L"API Key 已保存到 Windows Credential Manager；搜索框中的明文已清除。";
        } else reply = L"保存 API Key 失败，Windows 错误：" + std::to_wstring(GetLastError());
        return true;
    }
    if (lower == L"/clear-key") {
        if (SaveApiKey(L"")) reply = L"API Key 已删除。";
        else reply = L"删除 API Key 失败。";
        SaveConfig(config_);
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
        config_.baseUrl = baseUrl;
        config_.model = model;
        config_.providerId = Lower(baseUrl).find(L"deepseek") != std::wstring::npos ? L"deepseek" : L"openai-compatible";
        if (SaveConfig(config_)) reply = L"模型配置已保存：" + config_.model + L" @ " + config_.baseUrl;
        else reply = L"模型配置保存失败。";
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
        onDone(L"未配置 API Key。输入 /key <API Key> 保存；默认 DeepSeek 配置可直接使用。也可用 /provider <BaseURL> <Model> 切换兼容服务。");
        return;
    }

    URL_COMPONENTSW components{};
    components.dwStructSize = sizeof(components);
    components.dwSchemeLength = static_cast<DWORD>(-1);
    components.dwHostNameLength = static_cast<DWORD>(-1);
    components.dwUrlPathLength = static_cast<DWORD>(-1);
    components.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(config_.baseUrl.c_str(), 0, 0, &components)) {
        onDone(L"模型 Base URL 无效。请用 /provider <BaseURL> <Model> 重新设置。");
        return;
    }

    std::wstring host(components.lpszHostName, components.dwHostNameLength);
    std::wstring path;
    if (components.dwUrlPathLength) path.assign(components.lpszUrlPath, components.dwUrlPathLength);
    if (path.empty()) path = L"/";
    while (path.size() > 1 && path.back() == L'/') path.pop_back();
    if (!path.ends_with(L"/chat/completions")) {
        if (path == L"/") path.clear();
        path += L"/chat/completions";
    }

    HINTERNET session = WinHttpOpen(L"TuringDesk.Native/2.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) { onDone(WinHttpErrorText(GetLastError())); return; }
    WinHttpSetTimeouts(session, 5000, 5000, 15000, 60000);
    HINTERNET connect = WinHttpConnect(session, host.c_str(), components.nPort, 0);
    if (!connect) {
        const auto error = GetLastError();
        WinHttpCloseHandle(session);
        onDone(WinHttpErrorText(error));
        return;
    }

    const DWORD flags = components.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(connect, L"POST", path.c_str(), nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!request) {
        const auto error = GetLastError();
        WinHttpCloseHandle(connect); WinHttpCloseHandle(session);
        onDone(WinHttpErrorText(error));
        return;
    }
    activeRequest_.store(request);

    const std::wstring headers = L"Content-Type: application/json\r\nAccept: text/event-stream\r\nAuthorization: Bearer " + apiKey + L"\r\n";
    std::string body = "{\"model\":\"" + EscapeJsonUtf8(config_.model) +
        "\",\"messages\":[{\"role\":\"system\",\"content\":\"You are TuringDesk L3. Answer concisely. Never claim that you executed an OS action unless a registered native tool actually did so.\"},{\"role\":\"user\",\"content\":\"" +
        EscapeJsonUtf8(prompt) + "\"}],\"stream\":true";
    if (Lower(host).find(L"deepseek") != std::wstring::npos) body += ",\"thinking\":{\"type\":\"disabled\"}";
    body += "}";

    auto finishHandles = [&]() {
        if (activeRequest_.exchange(nullptr) == request) WinHttpCloseHandle(request);
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
    };

    if (!WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(-1L), body.data(), static_cast<DWORD>(body.size()), static_cast<DWORD>(body.size()), 0) ||
        !WinHttpReceiveResponse(request, nullptr)) {
        const auto error = GetLastError();
        finishHandles();
        onDone(stopToken.stop_requested() ? L"已停止" : WinHttpErrorText(error));
        return;
    }

    DWORD status = 0;
    DWORD statusSize = sizeof(status);
    WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &statusSize, WINHTTP_NO_HEADER_INDEX);

    std::string pending;
    std::string fullBody;
    bool emitted = false;
    bool doneMarker = false;
    while (!stopToken.stop_requested() && !doneMarker) {
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available)) break;
        if (available == 0) break;
        std::vector<char> buffer(available);
        DWORD read = 0;
        if (!WinHttpReadData(request, buffer.data(), available, &read)) break;
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
            if (data == "[DONE]") { doneMarker = true; break; }
            const auto content = ExtractJsonString(data, "\"content\"");
            if (!content.empty()) {
                const auto wide = Utf8ToWide(content);
                if (!wide.empty()) { emitted = true; onDelta(wide); }
            }
        }
    }

    finishHandles();
    if (stopToken.stop_requested()) { onDone(L"已停止"); return; }
    if (status < 200 || status >= 300) {
        onDone(L"模型请求失败：HTTP " + std::to_wstring(status));
        return;
    }
    if (!emitted) {
        const auto content = ExtractJsonString(fullBody, "\"content\"");
        if (!content.empty()) {
            const auto wide = Utf8ToWide(content);
            if (!wide.empty()) { onDelta(wide); emitted = true; }
        }
    }
    onDone(emitted ? L"" : L"模型返回成功，但没有可显示的 content。请检查兼容接口响应格式。");
}

} // namespace turingdesk
