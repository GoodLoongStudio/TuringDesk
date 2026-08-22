#include "turingdesk/DirectToolRuntime.h"
#include "turingdesk/NativeTools.h"
#include <wincred.h>
#include <windows.h>
#include <winhttp.h>
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cwctype>
#include <initializer_list>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

namespace turingdesk {
namespace {

constexpr wchar_t kCredentialTarget[] = L"TuringDesk/ModelApiKey";
constexpr std::size_t kMaxConversationTurns = 6;
constexpr int kMaxToolRounds = 4;

enum class SafeIntent {
    None,
    PptCreate,
    FileCreate,
    FolderList,
};

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

bool ContainsAny(const std::wstring& text, std::initializer_list<std::wstring_view> tokens) {
    for (const auto token : tokens) {
        if (text.find(token) != std::wstring::npos) return true;
    }
    return false;
}

bool HasCreateVerb(const std::wstring& lower) {
    return ContainsAny(lower, {
        L"创建", L"生成", L"新建", L"制作", L"做个", L"做一个", L"做一份", L"做份",
        L"帮我做", L"帮我制作", L"给我做", L"弄个", L"来个", L"create", L"generate", L"make", L"build"
    });
}

SafeIntent DetectSafeIntent(const std::wstring& prompt) {
    const auto lower = Lower(prompt);
    const bool ppt = ContainsAny(lower, {L"ppt", L"powerpoint", L"演示文稿", L"幻灯片"});
    if (ppt && HasCreateVerb(lower)) return SafeIntent::PptCreate;

    const bool location = ContainsAny(lower, {L"桌面", L"下载", L"文档", L"desktop", L"downloads", L"documents"});
    const bool listVerb = ContainsAny(lower, {L"列出", L"列一下", L"看看", L"查看", L"有哪些", L"有什么", L"list", L"show"});
    const bool folderNoun = ContainsAny(lower, {L"文件", L"目录", L"文件夹", L"file", L"folder"});
    if (location && listVerb && folderNoun) return SafeIntent::FolderList;

    const bool fileNoun = ContainsAny(lower, {L"文本文件", L"文件", L"txt", L"markdown", L".md", L".json", L".csv", L"text file"});
    if (!ppt && fileNoun && HasCreateVerb(lower)) return SafeIntent::FileCreate;
    return SafeIntent::None;
}

bool LooksLikeDesktopAction(const std::wstring& prompt) {
    const auto lower = Lower(prompt);
    return ContainsAny(lower, {
        L"ppt", L"powerpoint", L"演示文稿", L"幻灯片", L"创建", L"生成", L"新建", L"制作", L"打开",
        L"桌面", L"下载", L"文档", L"文件", L"应用", L"create", L"generate", L"make", L"open", L"desktop", L"file", L"app"
    });
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

    HINTERNET session = WinHttpOpen(L"TuringDesk.DirectAgent/2.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
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

std::wstring StripCodeFence(std::wstring text) {
    text = Trim(std::move(text));
    if (!text.starts_with(L"```")) return text;
    const auto firstNewline = text.find(L'\n');
    if (firstNewline != std::wstring::npos) text.erase(0, firstNewline + 1);
    const auto lastFence = text.rfind(L"```");
    if (lastFence != std::wstring::npos) text.erase(lastFence);
    return Trim(std::move(text));
}

std::string InferLocation(const std::wstring& prompt) {
    const auto lower = Lower(prompt);
    if (ContainsAny(lower, {L"下载", L"downloads", L"download"})) return "downloads";
    if (ContainsAny(lower, {L"文档", L"documents", L"document"})) return "documents";
    return "desktop";
}

std::wstring InferFileName(const std::wstring& prompt) {
    const auto lower = Lower(prompt);
    for (const std::wstring_view ext : {L".txt", L".md", L".json", L".csv"}) {
        const auto pos = lower.find(ext);
        if (pos == std::wstring::npos) continue;
        std::size_t begin = pos;
        while (begin > 0) {
            const wchar_t ch = prompt[begin - 1];
            if (std::iswspace(ch) || ch == L'"' || ch == L'\'' || ch == L'，' || ch == L'。' || ch == L'：' || ch == L':' || ch == L'/' || ch == L'\\') break;
            --begin;
        }
        std::wstring name = prompt.substr(begin, pos + ext.size() - begin);
        name = Trim(std::move(name));
        if (!name.empty() && name.size() <= 120) return name;
    }
    if (ContainsAny(lower, {L"markdown", L"md 文件", L"md文件"})) return L"TuringDesk-Generated.md";
    if (lower.find(L"json") != std::wstring::npos) return L"TuringDesk-Generated.json";
    if (lower.find(L"csv") != std::wstring::npos) return L"TuringDesk-Generated.csv";
    return L"TuringDesk-Generated.txt";
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

bool GeneratePlainText(const ModelConfig& config, const std::wstring& apiUrl, const std::wstring& apiKey,
                       std::wstring_view systemPrompt, const std::wstring& userPrompt,
                       std::atomic<HINTERNET>& activeRequest, std::stop_token stopToken,
                       std::wstring& output, std::wstring& error) {
    std::vector<std::string> messages;
    messages.push_back("{\"role\":\"system\",\"content\":\"" + EscapeJson(systemPrompt) + "\"}");
    messages.push_back("{\"role\":\"user\",\"content\":\"" + EscapeJson(userPrompt) + "\"}");
    const auto response = PostJson(apiUrl, apiKey, BuildRequest(config, messages, false), activeRequest, stopToken);
    if (response.stopped) { error = L"已停止"; return false; }
    if (!response.ok || response.status < 200 || response.status >= 300) {
        error = HttpErrorText(response, apiUrl);
        return false;
    }
    output = ParseReply(response.body).text;
    if (output.empty()) {
        error = L"模型没有返回可用于本地工具的内容。";
        return false;
    }
    return true;
}

bool ExecuteSafeIntent(SafeIntent intent, const ModelConfig& config, const std::wstring& apiUrl, const std::wstring& apiKey,
                       const std::wstring& prompt, std::atomic<HINTERNET>& activeRequest, std::stop_token stopToken,
                       const DirectToolRuntime::DeltaCallback& onDelta, std::wstring& finalText, std::wstring& error) {
    if (intent == SafeIntent::FolderList) {
        const std::string args = "{\"location\":\"" + InferLocation(prompt) + "\"}";
        const auto native = ExecuteNativeTool("folder_list", args);
        onDelta(L"[Native Tool · folder_list] " + native.message + L"\r\n");
        if (!native.success) { error = native.message; return false; }
        finalText = native.message;
        return true;
    }

    if (intent == SafeIntent::PptCreate) {
        std::wstring outline;
        if (!GeneratePlainText(config, apiUrl, apiKey,
                               L"Create concise slide content for the user's presentation request. Return ONLY slide markdown. Each slide begins with '# title' followed by short bullet lines. No preface and no code fence.",
                               prompt, activeRequest, stopToken, outline, error)) return false;
        outline = StripCodeFence(std::move(outline));
        const auto native = ExecuteNativeTool("ppt_create", PptArgumentsFromOutline(outline));
        onDelta(L"[Native Tool · ppt_create] " + native.message + L"\r\n");
        if (!native.success) { error = native.message; return false; }
        finalText = native.message;
        return true;
    }

    if (intent == SafeIntent::FileCreate) {
        std::wstring content;
        if (!GeneratePlainText(config, apiUrl, apiKey,
                               L"Write the exact contents for the local text file requested by the user. Return ONLY file contents with no preface and no code fence.",
                               prompt, activeRequest, stopToken, content, error)) return false;
        content = StripCodeFence(std::move(content));
        const auto fileName = InferFileName(prompt);
        const std::string args = "{\"location\":\"" + InferLocation(prompt) + "\",\"file_name\":\"" +
                                 EscapeJson(fileName) + "\",\"content\":\"" + EscapeJson(content) + "\"}";
        const auto native = ExecuteNativeTool("file_create", args);
        onDelta(L"[Native Tool · file_create] " + native.message + L"\r\n");
        if (!native.success) { error = native.message; return false; }
        finalText = native.message;
        return true;
    }

    error = L"没有匹配到安全 Native Tool。";
    return false;
}

} // namespace

DirectToolRuntime::~DirectToolRuntime() {
    ResetSession();
}

bool DirectToolRuntime::SelfTest() {
    if (DetectSafeIntent(L"帮我做个ppt，放桌面上") != SafeIntent::PptCreate) return false;
    if (DetectSafeIntent(L"随便做一份 PowerPoint 放在桌面") != SafeIntent::PptCreate) return false;
    if (DetectSafeIntent(L"制作一个演示文稿") != SafeIntent::PptCreate) return false;
    if (DetectSafeIntent(L"列一下桌面文件") != SafeIntent::FolderList) return false;
    if (DetectSafeIntent(L"看看下载目录有什么文件") != SafeIntent::FolderList) return false;
    if (DetectSafeIntent(L"在桌面新建一个 notes.txt 文本文件") != SafeIntent::FileCreate) return false;
    if (DetectSafeIntent(L"你是谁") != SafeIntent::None) return false;
    if (DetectSafeIntent(L"打开桌面上的 setup.exe") != SafeIntent::None) return false;
    return true;
}

bool DirectToolRuntime::CanHandle(const L3Agent& agent) const {
    if (agent.Config().providerId == L"anthropic") return false;
    const auto url = Trim(agent.CurrentApiUrl());
    if (url.empty() || agent.Config().model.empty()) return false;
    return EndsWithInsensitive(url, L"/chat/completions") || agent.Config().providerId != L"unconfigured";
}

std::wstring DirectToolRuntime::StatusText(const L3Agent& agent) const {
    if (!CanHandle(agent)) return L"Direct Agent Runtime 不适用于当前 Provider";
    return L"Direct Agent Runtime V2 · Native Intent Router + OpenAI-compatible tools · Safe fallback=3";
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
    if (!CanHandle(agent)) { onDone(L"当前 Provider 暂不支持 Direct Agent Runtime。"); return; }
    if (apiKey.empty() && !IsLocalUrl(apiUrl)) { onDone(L"未配置 API Key。请打开右上角 AI 设置填写 Key。"); return; }

    const auto safeIntent = DetectSafeIntent(prompt);
    if (safeIntent != SafeIntent::None) {
        std::wstring finalText;
        std::wstring error;
        onDelta(L"[Native Intent] 已识别安全桌面动作，准备执行…\r\n");
        if (!ExecuteSafeIntent(safeIntent, config, apiUrl, apiKey, prompt, activeRequest_, stopToken, onDelta, finalText, error)) {
            onDone(error);
            return;
        }
        {
            std::scoped_lock lock(conversationMutex_);
            conversation_.push_back({std::move(prompt), finalText});
            if (conversation_.size() > kMaxConversationTurns) {
                conversation_.erase(conversation_.begin(), conversation_.begin() + (conversation_.size() - kMaxConversationTurns));
            }
        }
        onDone(L"");
        return;
    }

    std::vector<ChatTurn> history;
    {
        std::scoped_lock lock(conversationMutex_);
        history = conversation_;
    }

    std::vector<std::string> messages;
    messages.push_back(R"JSON({"role":"system","content":"You are TuringDesk Native Agent. For supported desktop actions, use the provided native tools. Never claim an OS action succeeded until a tool result confirms success. Use ppt_create for real PowerPoint creation, file_create for safe text files, folder_list for safe folder inspection, and file_open only when the user explicitly asks to open a known file. Do not invent shell execution."})JSON");
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
                (response.status == 400 || response.status == 404 || response.status == 405 || response.status == 415 || response.status == 422);
            if (toolSyntaxRejected && !LooksLikeDesktopAction(prompt)) {
                const auto plain = PostJson(apiUrl, apiKey, BuildRequest(config, messages, false), activeRequest_, stopToken);
                if (!plain.ok || plain.status < 200 || plain.status >= 300) { onDone(HttpErrorText(plain, apiUrl)); return; }
                finalText = ParseReply(plain.body).text;
                if (!finalText.empty()) onDelta(finalText);
                break;
            }
            if (toolSyntaxRejected && LooksLikeDesktopAction(prompt)) {
                onDone(L"这个桌面动作还没有安全 Native Intent fallback。TuringDesk 不会把模型文本伪装成本地执行结果。需要新增对应 Native Tool 或 Approval 后才能执行。");
                return;
            }
            onDone(HttpErrorText(response, apiUrl));
            return;
        }

        const auto parsed = ParseReply(response.body);
        if (parsed.calls.empty()) {
            finalText = parsed.text;
            if (!toolExecuted && LooksLikeDesktopAction(prompt)) {
                onDone(L"模型没有发出工具调用，而且这个动作没有匹配到安全 Native Intent。TuringDesk 已停止本地动作，不会伪装执行成功。");
                return;
            }
            if (!finalText.empty()) onDelta(finalText);
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
