#include "turingdesk/DirectToolRuntime.h"
#include "turingdesk/NativeTools.h"
#include <wincred.h>
#include <windows.h>
#include <winhttp.h>
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cwctype>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

namespace turingdesk {
namespace {

constexpr wchar_t kCredentialTarget[] = L"TuringDesk/ModelApiKey";
constexpr std::size_t kMaxConversationTurns = 6;
constexpr int kMaxToolRounds = 4;

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

std::string WideToUtf8(std::wstring_view value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string out(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(std::string_view value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring out(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count);
    return out;
}

std::string EscapeJsonUtf8(std::string_view value) {
    std::string out;
    out.reserve(value.size() + 16);
    for (unsigned char ch : value) {
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

std::string EscapeJson(std::wstring_view value) {
    return EscapeJsonUtf8(WideToUtf8(value));
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
    ++pos;
    while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) ++pos;
    if (pos >= json.size() || json[pos] != '"') return {};
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

std::string_view ExtractContainer(std::string_view json, std::string_view key, char open, char close) {
    auto pos = json.find(key);
    if (pos == std::string_view::npos) return {};
    pos = json.find(':', pos + key.size());
    if (pos == std::string_view::npos) return {};
    ++pos;
    while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) ++pos;
    if (pos >= json.size() || json[pos] != open) return {};

    const std::size_t start = pos;
    int depth = 0;
    bool inString = false;
    bool escaped = false;
    for (; pos < json.size(); ++pos) {
        const char ch = json[pos];
        if (inString) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') inString = false;
            continue;
        }
        if (ch == '"') { inString = true; continue; }
        if (ch == open) ++depth;
        else if (ch == close) {
            --depth;
            if (depth == 0) return json.substr(start, pos - start + 1);
        }
    }
    return {};
}

std::vector<std::string_view> SplitObjects(std::string_view container) {
    std::vector<std::string_view> out;
    int depth = 0;
    bool inString = false;
    bool escaped = false;
    std::size_t start = std::string_view::npos;
    for (std::size_t i = 0; i < container.size(); ++i) {
        const char ch = container[i];
        if (inString) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') inString = false;
            continue;
        }
        if (ch == '"') { inString = true; continue; }
        if (ch == '{') {
            if (depth == 0) start = i;
            ++depth;
        } else if (ch == '}') {
            --depth;
            if (depth == 0 && start != std::string_view::npos) {
                out.push_back(container.substr(start, i - start + 1));
                start = std::string_view::npos;
            }
        }
    }
    return out;
}

void ReplaceAll(std::string& text, std::string_view from, std::string_view to) {
    std::size_t pos = 0;
    while ((pos = text.find(from, pos)) != std::string::npos) {
        text.replace(pos, from.size(), to);
        pos += to.size();
    }
}

std::string OpenAiToolsJson() {
    const std::string canonical = NativeToolDefinitionsJson();
    std::string out = "[";
    bool first = true;
    for (const auto objectView : SplitObjects(canonical)) {
        std::string object(objectView);
        constexpr std::string_view prefix = "{\"type\":\"function\",";
        if (!object.starts_with(prefix)) continue;
        object = "{" + object.substr(prefix.size());
        ReplaceAll(object, "\"inputSchema\"", "\"parameters\"");
        if (!first) out += ',';
        out += "{\"type\":\"function\",\"function\":" + object + "}";
        first = false;
    }
    out += ']';
    return out;
}

std::wstring LoadApiKey() {
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

bool IsLocalUrl(const std::wstring& raw) {
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(raw.c_str(), 0, 0, &parts) || !parts.lpszHostName) return false;
    std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    host = Lower(host);
    return host == L"localhost" || host == L"127.0.0.1" || host == L"::1";
}

struct HttpResult {
    bool ok{};
    bool stopped{};
    DWORD status{};
    DWORD error{};
    std::string body;
};

HttpResult PostJson(const std::wstring& url, const std::wstring& apiKey, const std::string& body,
                    std::atomic<HINTERNET>& activeRequest, std::stop_token stopToken) {
    HttpResult result;
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwSchemeLength = static_cast<DWORD>(-1);
    parts.dwHostNameLength = static_cast<DWORD>(-1);
    parts.dwUrlPathLength = static_cast<DWORD>(-1);
    parts.dwExtraInfoLength = static_cast<DWORD>(-1);
    if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts) || !parts.lpszHostName) {
        result.error = ERROR_INVALID_PARAMETER;
        return result;
    }

    const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
    std::wstring path = L"/";
    if (parts.lpszUrlPath && parts.dwUrlPathLength) path.assign(parts.lpszUrlPath, parts.dwUrlPathLength);
    if (parts.lpszExtraInfo && parts.dwExtraInfoLength) path.append(parts.lpszExtraInfo, parts.dwExtraInfoLength);

    HINTERNET session = WinHttpOpen(L"TuringDesk.DirectAgent/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                                    WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) { result.error = GetLastError(); return result; }
    WinHttpSetTimeouts(session, 5000, 5000, 15000, 60000);
    HINTERNET connection = WinHttpConnect(session, host.c_str(), parts.nPort, 0);
    if (!connection) {
        result.error = GetLastError();
        WinHttpCloseHandle(session);
        return result;
    }

    const DWORD flags = parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(connection, L"POST", path.c_str(), nullptr, WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!request) {
        result.error = GetLastError();
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return result;
    }
    activeRequest.store(request);

    std::wstring headers = L"Content-Type: application/json\r\nAccept: application/json\r\n";
    if (!apiKey.empty()) headers += L"Authorization: Bearer " + apiKey + L"\r\n";
    if (!WinHttpSendRequest(request, headers.c_str(), static_cast<DWORD>(-1L),
                            const_cast<char*>(body.data()), static_cast<DWORD>(body.size()),
                            static_cast<DWORD>(body.size()), 0) ||
        !WinHttpReceiveResponse(request, nullptr)) {
        result.error = GetLastError();
        result.stopped = stopToken.stop_requested();
    } else {
        DWORD bytes = sizeof(result.status);
        WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX, &result.status, &bytes, WINHTTP_NO_HEADER_INDEX);
        while (!stopToken.stop_requested()) {
            DWORD available = 0;
            if (!WinHttpQueryDataAvailable(request, &available)) { result.error = GetLastError(); break; }
            if (!available) { result.ok = true; break; }
            std::vector<char> buffer(available);
            DWORD read = 0;
            if (!WinHttpReadData(request, buffer.data(), available, &read)) { result.error = GetLastError(); break; }
            if (!read) { result.ok = true; break; }
            if (result.body.size() + read <= 8 * 1024 * 1024) result.body.append(buffer.data(), read);
        }
        result.stopped = stopToken.stop_requested();
    }

    if (activeRequest.exchange(nullptr) == request) WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return result;
}

struct ToolCall {
    std::string id;
    std::string name;
    std::string arguments;
};

struct AssistantReply {
    std::wstring text;
    std::vector<ToolCall> calls;
};

AssistantReply ParseReply(const std::string& body) {
    AssistantReply reply;
    const auto choices = ExtractContainer(body, "\"choices\"", '[', ']');
    const auto choiceObjects = SplitObjects(choices);
    if (choiceObjects.empty()) return reply;
    const auto message = ExtractContainer(choiceObjects.front(), "\"message\"", '{', '}');
    if (message.empty()) return reply;
    reply.text = Utf8ToWide(ExtractJsonString(message, "\"content\""));
    const auto toolCalls = ExtractContainer(message, "\"tool_calls\"", '[', ']');
    for (const auto object : SplitObjects(toolCalls)) {
        ToolCall call;
        call.id = ExtractJsonString(object, "\"id\"");
        const auto function = ExtractContainer(object, "\"function\"", '{', '}');
        call.name = ExtractJsonString(function, "\"name\"");
        call.arguments = ExtractJsonString(function, "\"arguments\"");
        if (!call.name.empty()) reply.calls.push_back(std::move(call));
    }
    return reply;
}

std::string JoinMessages(const std::vector<std::string>& messages) {
    std::string out = "[";
    for (std::size_t i = 0; i < messages.size(); ++i) {
        if (i) out += ',';
        out += messages[i];
    }
    out += ']';
    return out;
}

std::string BuildRequest(const ModelConfig& config, const std::vector<std::string>& messages, bool tools) {
    std::string body = "{\"model\":\"" + EscapeJson(config.model) + "\",\"messages\":" + JoinMessages(messages);
    if (tools) body += ",\"tools\":" + OpenAiToolsJson() + ",\"tool_choice\":\"auto\"";
    body += ",\"stream\":false}";
    return body;
}

std::string AssistantToolMessage(const std::vector<ToolCall>& calls) {
    std::string out = "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[";
    for (std::size_t i = 0; i < calls.size(); ++i) {
        if (i) out += ',';
        const std::string id = calls[i].id.empty() ? "call_td_" + std::to_string(i + 1) : calls[i].id;
        out += "{\"id\":\"" + EscapeJsonUtf8(id) + "\",\"type\":\"function\",\"function\":{\"name\":\"" +
               EscapeJsonUtf8(calls[i].name) + "\",\"arguments\":\"" + EscapeJsonUtf8(calls[i].arguments) + "\"}}";
    }
    out += "]}";
    return out;
}

bool LooksLikeDesktopAction(const std::wstring& prompt) {
    const auto lower = Lower(prompt);
    for (const wchar_t* token : {L"ppt", L"powerpoint", L"演示文稿", L"创建", L"生成", L"新建", L"打开", L"桌面", L"文件",
                                 L"create", L"generate", L"make", L"open", L"desktop", L"file"}) {
        if (lower.find(token) != std::wstring::npos) return true;
    }
    return false;
}

bool LooksLikePptCreate(const std::wstring& prompt) {
    const auto lower = Lower(prompt);
    const bool ppt = lower.find(L"ppt") != std::wstring::npos || lower.find(L"powerpoint") != std::wstring::npos ||
                     lower.find(L"演示文稿") != std::wstring::npos;
    const bool create = lower.find(L"创建") != std::wstring::npos || lower.find(L"生成") != std::wstring::npos ||
                        lower.find(L"新建") != std::wstring::npos || lower.find(L"做一个") != std::wstring::npos ||
                        lower.find(L"create") != std::wstring::npos || lower.find(L"generate") != std::wstring::npos ||
                        lower.find(L"make") != std::wstring::npos;
    return ppt && create;
}

std::string PptArgumentsFromOutline(const std::wstring& outline) {
    const std::wstring safeOutline = outline.empty()
        ? L"# TuringDesk 演示\n- 由 TuringDesk Native Agent 自动创建\n# 下一步\n- 继续在 L3 中描述你想修改的内容"
        : outline;
    return "{\"file_name\":\"TuringDesk-Generated.pptx\",\"title\":\"TuringDesk 自动生成演示\","
           "\"subtitle\":\"由 TuringDesk Native Agent 创建\",\"slides_markdown\":\"" + EscapeJson(safeOutline) +
           "\",\"open_after_create\":true}";
}

std::wstring HttpErrorText(const HttpResult& result, const std::wstring& url) {
    if (result.stopped) return L"已停止";
    if (!result.ok) return L"Direct Agent 网络失败，WinHTTP=" + std::to_wstring(result.error);
    std::wstring text = L"Direct Agent 请求失败：HTTP " + std::to_wstring(result.status) + L" · " + url;
    if (!result.body.empty()) {
        const auto detail = Utf8ToWide(result.body.substr(0, 360));
        if (!detail.empty()) text += L" · " + detail;
    }
    return text;
}

} // namespace

DirectToolRuntime::~DirectToolRuntime() {
    ResetSession();
}

bool DirectToolRuntime::CanHandle(const L3Agent& agent) const {
    if (agent.Config().providerId == L"anthropic") return false;
    const auto url = Trim(agent.CurrentApiUrl());
    if (url.empty() || agent.Config().model.empty()) return false;
    return EndsWithInsensitive(url, L"/chat/completions") || agent.Config().providerId != L"unconfigured";
}

std::wstring DirectToolRuntime::StatusText(const L3Agent& agent) const {
    if (!CanHandle(agent)) return L"Direct Agent Tool Runtime 不适用于当前 Provider";
    return L"Direct Agent Tool Runtime · OpenAI-compatible tools + safe fallback · Native Tools=4";
}

void DirectToolRuntime::AskAsync(const L3Agent& agent, std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone) {
    Stop();
    if (worker_.joinable()) worker_.join();
    busy_.store(true);
    worker_ = std::jthread([this, &agent, prompt = std::move(prompt), onDelta = std::move(onDelta), onDone = std::move(onDone)](std::stop_token token) mutable {
        RunRequest(agent, std::move(prompt), std::move(onDelta), std::move(onDone), token);
        busy_.store(false);
    });
}

void DirectToolRuntime::Stop() {
    if (worker_.joinable()) worker_.request_stop();
    if (auto request = activeRequest_.exchange(nullptr)) WinHttpCloseHandle(request);
}

void DirectToolRuntime::ResetSession() {
    Stop();
    if (worker_.joinable()) worker_.join();
    busy_.store(false);
    std::scoped_lock lock(conversationMutex_);
    conversation_.clear();
}

void DirectToolRuntime::RunRequest(const L3Agent& agent, std::wstring prompt,
                                   DeltaCallback onDelta, DoneCallback onDone, std::stop_token stopToken) {
    const auto config = agent.Config();
    const auto apiUrl = Trim(agent.CurrentApiUrl());
    const auto apiKey = LoadApiKey();
    if (!CanHandle(agent)) { onDone(L"当前 Provider 暂不支持 Direct Agent Tool Runtime。"); return; }
    if (apiKey.empty() && !IsLocalUrl(apiUrl)) { onDone(L"未配置 API Key。请打开右上角 AI 设置填写 Key。"); return; }

    std::vector<ChatTurn> history;
    {
        std::scoped_lock lock(conversationMutex_);
        history = conversation_;
    }

    std::vector<std::string> messages;
    messages.push_back(R"JSON({"role":"system","content":"You are TuringDesk Native Agent. For supported desktop actions, use the provided native tools. Never claim an OS action succeeded until a tool result confirms success. Use ppt_create for real PowerPoint creation, file_create for safe text files, folder_list for safe folder inspection, and file_open for opening files. Do not invent shell execution."})JSON");
    for (const auto& turn : history) {
        messages.push_back("{\"role\":\"user\",\"content\":\"" + EscapeJson(turn.user) + "\"}");
        messages.push_back("{\"role\":\"assistant\",\"content\":\"" + EscapeJson(turn.assistant) + "\"}");
    }
    messages.push_back("{\"role\":\"user\",\"content\":\"" + EscapeJson(prompt) + "\"}");

    bool toolExecuted = false;
    std::wstring finalText;
    std::wstring lastToolResult;

    for (int round = 0; round < kMaxToolRounds && !stopToken.stop_requested(); ++round) {
        const auto response = PostJson(apiUrl, apiKey, BuildRequest(config, messages, true), activeRequest_, stopToken);
        if (response.stopped) { onDone(L"已停止"); return; }

        if (!response.ok || response.status < 200 || response.status >= 300) {
            const bool toolSyntaxRejected = round == 0 && response.ok &&
                                            (response.status == 400 || response.status == 404 || response.status == 422);
            if (toolSyntaxRejected && LooksLikePptCreate(prompt)) {
                std::vector<std::string> outlineMessages;
                outlineMessages.push_back(R"JSON({"role":"system","content":"Create concise slide content for the user's presentation request. Return ONLY slide markdown: each slide begins with '# title', followed by short bullet lines. No preface, no code fence."})JSON");
                outlineMessages.push_back("{\"role\":\"user\",\"content\":\"" + EscapeJson(prompt) + "\"}");
                const auto plain = PostJson(apiUrl, apiKey, BuildRequest(config, outlineMessages, false), activeRequest_, stopToken);
                std::wstring outline;
                if (plain.ok && plain.status >= 200 && plain.status < 300) outline = ParseReply(plain.body).text;
                const auto native = ExecuteNativeTool("ppt_create", PptArgumentsFromOutline(outline));
                onDelta(L"[Native Tool · ppt_create] " + native.message + L"\r\n");
                if (!native.success) { onDone(native.message); return; }
                toolExecuted = true;
                lastToolResult = native.message;
                finalText = L"已生成并打开真实 PPT 文件。";
                onDelta(finalText);
                break;
            }
            if (toolSyntaxRejected && !LooksLikeDesktopAction(prompt)) {
                const auto plain = PostJson(apiUrl, apiKey, BuildRequest(config, messages, false), activeRequest_, stopToken);
                if (!plain.ok || plain.status < 200 || plain.status >= 300) { onDone(HttpErrorText(plain, apiUrl)); return; }
                finalText = ParseReply(plain.body).text;
                if (!finalText.empty()) onDelta(finalText);
                break;
            }
            onDone(HttpErrorText(response, apiUrl));
            return;
        }

        const auto parsed = ParseReply(response.body);
        if (parsed.calls.empty()) {
            if (!parsed.text.empty()) finalText = parsed.text;
            if (!toolExecuted && LooksLikePptCreate(prompt)) {
                const auto native = ExecuteNativeTool("ppt_create", PptArgumentsFromOutline(parsed.text));
                onDelta(L"[Native Tool · ppt_create] " + native.message + L"\r\n");
                if (!native.success) { onDone(native.message); return; }
                toolExecuted = true;
                lastToolResult = native.message;
                finalText = L"已生成并打开真实 PPT 文件。";
                onDelta(finalText);
            } else if (!toolExecuted && LooksLikeDesktopAction(prompt)) {
                onDone(L"模型没有调用 Native Tool。为避免伪装成本地执行结果，本次桌面动作已停止。当前 Provider 可能不支持 tools/function calling。");
                return;
            } else if (!finalText.empty()) {
                onDelta(finalText);
            }
            break;
        }

        toolExecuted = true;
        messages.push_back(AssistantToolMessage(parsed.calls));
        for (std::size_t i = 0; i < parsed.calls.size(); ++i) {
            auto call = parsed.calls[i];
            if (call.id.empty()) call.id = "call_td_" + std::to_string(i + 1);
            const auto native = ExecuteNativeTool(call.name, call.arguments);
            lastToolResult = native.message;
            onDelta(L"[Native Tool · " + Utf8ToWide(call.name) + L"] " + native.message + L"\r\n");
            messages.push_back("{\"role\":\"tool\",\"tool_call_id\":\"" + EscapeJsonUtf8(call.id) +
                               "\",\"content\":\"" + EscapeJson(native.message) + "\"}");
        }
    }

    if (stopToken.stop_requested()) { onDone(L"已停止"); return; }
    if (finalText.empty() && toolExecuted) finalText = lastToolResult;
    if (finalText.empty()) { onDone(L"Direct Agent 返回成功，但没有文本或工具结果。"); return; }

    {
        std::scoped_lock lock(conversationMutex_);
        conversation_.push_back({std::move(prompt), finalText});
        if (conversation_.size() > kMaxConversationTurns) {
            conversation_.erase(conversation_.begin(), conversation_.begin() + (conversation_.size() - kMaxConversationTurns));
        }
    }
    onDone(L"");
}

} // namespace turingdesk
