#include "turingdesk/CodexRuntime.h"
#include "turingdesk/NativeTools.h"
#include <wincred.h>
#include <algorithm>
#include <cstdio>
#include <cwchar>
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
constexpr wchar_t kApiKeyEnvironment[] = L"TURINGDESK_MODEL_API_KEY";

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

bool HasResponseId(std::string_view json, long long id) {
    auto pos = json.find("\"id\"");
    if (pos == std::string_view::npos) return false;
    pos = json.find(':', pos + 4);
    if (pos == std::string_view::npos) return false;
    ++pos;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t')) ++pos;
    bool negative = false;
    if (pos < json.size() && json[pos] == '-') { negative = true; ++pos; }
    long long value = 0;
    bool any = false;
    while (pos < json.size() && json[pos] >= '0' && json[pos] <= '9') {
        any = true;
        value = value * 10 + (json[pos] - '0');
        ++pos;
    }
    if (negative) value = -value;
    return any && value == id;
}

bool TryReadRequestId(std::string_view json, long long& id) {
    auto pos = json.find("\"id\"");
    if (pos == std::string_view::npos) return false;
    pos = json.find(':', pos + 4);
    if (pos == std::string_view::npos) return false;
    ++pos;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t')) ++pos;
    bool negative = false;
    if (pos < json.size() && json[pos] == '-') { negative = true; ++pos; }
    long long value = 0;
    bool any = false;
    while (pos < json.size() && json[pos] >= '0' && json[pos] <= '9') {
        any = true;
        value = value * 10 + (json[pos] - '0');
        ++pos;
    }
    if (!any) return false;
    id = negative ? -value : value;
    return true;
}

std::wstring TomlEscape(const std::wstring& value) {
    std::wstring out;
    out.reserve(value.size() + 8);
    for (wchar_t ch : value) {
        if (ch == L'\\' || ch == L'"') out.push_back(L'\\');
        if (ch == L'\n') { out += L"\\n"; continue; }
        if (ch == L'\r') { out += L"\\r"; continue; }
        out.push_back(ch);
    }
    return out;
}

fs::path ModuleDirectory() {
    std::wstring path(32768, L'\0');
    const DWORD count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (count == 0 || count >= path.size()) return {};
    path.resize(count);
    return fs::path(path).parent_path();
}

fs::path CodexHomeDirectory() {
    wchar_t localAppData[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
    if (count > 0 && count < std::size(localAppData)) return fs::path(localAppData) / L"TuringDesk" / L"CodexHome";
    return fs::temp_directory_path() / L"TuringDesk" / L"CodexHome";
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

std::wstring DesktopDirectory() {
    wchar_t profile[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"USERPROFILE", profile, static_cast<DWORD>(std::size(profile)));
    if (count > 0 && count < std::size(profile)) {
        const auto desktop = fs::path(profile) / L"Desktop";
        std::error_code ec;
        if (fs::exists(desktop, ec) && fs::is_directory(desktop, ec)) return desktop.wstring();
    }
    return ModuleDirectory().wstring();
}

std::vector<wchar_t> BuildEnvironmentBlock(const std::wstring& codexHome, const std::wstring& apiKey) {
    std::vector<std::wstring> entries;
    LPWCH block = GetEnvironmentStringsW();
    if (block) {
        for (const wchar_t* cursor = block; *cursor != L'\0'; cursor += std::wcslen(cursor) + 1) {
            std::wstring entry(cursor);
            const auto equal = entry.find(L'=');
            const std::wstring name = equal == std::wstring::npos ? entry : entry.substr(0, equal);
            const auto lower = Lower(name);
            if (lower == L"codex_home" || lower == L"turingdesk_model_api_key") continue;
            entries.push_back(std::move(entry));
        }
        FreeEnvironmentStringsW(block);
    }
    entries.push_back(L"CODEX_HOME=" + codexHome);
    entries.push_back(std::wstring(kApiKeyEnvironment) + L"=" + apiKey);
    std::sort(entries.begin(), entries.end(), [](const std::wstring& a, const std::wstring& b) {
        return _wcsicmp(a.c_str(), b.c_str()) < 0;
    });

    std::size_t chars = 1;
    for (const auto& entry : entries) chars += entry.size() + 1;
    std::vector<wchar_t> out(chars, L'\0');
    wchar_t* cursor = out.data();
    for (const auto& entry : entries) {
        std::copy(entry.begin(), entry.end(), cursor);
        cursor += entry.size();
        *cursor++ = L'\0';
    }
    *cursor = L'\0';
    return out;
}

std::wstring StripResponsesSuffix(std::wstring url) {
    url = Trim(std::move(url));
    while (url.size() > 1 && url.back() == L'/') url.pop_back();
    if (EndsWithInsensitive(url, L"/responses")) url.resize(url.size() - 10);
    while (url.size() > 1 && url.back() == L'/') url.pop_back();
    return url;
}

std::wstring OpenAiResponsesBase(const L3Agent& agent) {
    auto url = agent.CurrentApiUrl();
    if (url.empty()) return {};
    const auto provider = Lower(agent.Config().providerId);
    if (EndsWithInsensitive(url, L"/responses")) return StripResponsesSuffix(url);
    if (provider != L"openai") return {};
    if (EndsWithInsensitive(url, L"/chat/completions")) {
        url.resize(url.size() - std::wstring(L"/chat/completions").size());
        while (url.size() > 1 && url.back() == L'/') url.pop_back();
        return url;
    }
    return {};
}

std::wstring SearchExecutable(const wchar_t* name) {
    std::wstring buffer(32768, L'\0');
    const DWORD count = SearchPathW(nullptr, name, nullptr, static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (count == 0 || count >= buffer.size()) return {};
    buffer.resize(count);
    return buffer;
}

} // namespace

CodexRuntime::~CodexRuntime() {
    ResetSession();
}

CodexRuntime::ProviderSetup CodexRuntime::BuildProviderSetup(const L3Agent& agent) const {
    ProviderSetup setup;
    setup.model = Trim(agent.Config().model);
    setup.baseUrl = OpenAiResponsesBase(agent);
    setup.apiKey = LoadApiKey();
    if (setup.model.empty()) {
        setup.message = L"模型未配置";
        return setup;
    }
    if (setup.baseUrl.empty()) {
        setup.message = L"当前 Provider 还没有 Responses 协议桥，继续使用 Direct Runtime";
        return setup;
    }
    if (setup.apiKey.empty()) {
        setup.message = L"Codex Runtime 当前需要 API Key";
        return setup;
    }
    setup.signature = setup.baseUrl + L"\n" + setup.model;
    setup.ok = true;
    return setup;
}

std::wstring CodexRuntime::FindBinary(bool& isCliBinary) const {
    isCliBinary = false;

    wchar_t explicitPath[32768]{};
    const DWORD explicitCount = GetEnvironmentVariableW(L"TURINGDESK_CODEX_APP_SERVER", explicitPath,
                                                         static_cast<DWORD>(std::size(explicitPath)));
    if (explicitCount > 0 && explicitCount < std::size(explicitPath)) {
        std::error_code ec;
        if (fs::exists(explicitPath, ec)) return explicitPath;
    }

    const auto bundled = ModuleDirectory() / L"Codex" / L"codex-app-server.exe";
    std::error_code ec;
    if (fs::exists(bundled, ec)) return bundled.wstring();

    auto found = SearchExecutable(L"codex-app-server.exe");
    if (!found.empty()) return found;

    found = SearchExecutable(L"codex.exe");
    if (!found.empty()) {
        isCliBinary = true;
        return found;
    }
    return {};
}

CodexRuntimeStatus CodexRuntime::Status(const L3Agent& agent) const {
    CodexRuntimeStatus status;
    bool cli = false;
    status.binaryPath = FindBinary(cli);
    status.binaryAvailable = !status.binaryPath.empty();
    const auto setup = BuildProviderSetup(agent);
    status.providerCompatible = setup.ok;
    {
        std::scoped_lock lock(processMutex_);
        status.running = process_ != nullptr;
    }
    if (!status.binaryAvailable) status.message = L"Codex sidecar 未安装；当前使用 Direct Runtime";
    else if (!status.providerCompatible) status.message = setup.message;
    else if (status.running) status.message = L"Codex Agent Runtime 正在运行";
    else status.message = L"Codex Agent Runtime 可用，将在下一次请求时按需启动";
    return status;
}

bool CodexRuntime::CanHandle(const L3Agent& agent) const {
    bool cli = false;
    return !FindBinary(cli).empty() && BuildProviderSetup(agent).ok;
}

bool CodexRuntime::ConfigureCodexHome(const ProviderSetup& setup, std::wstring& codexHome, std::wstring& error) const {
    const auto home = CodexHomeDirectory();
    std::error_code ec;
    fs::create_directories(home, ec);
    if (ec) {
        error = L"无法创建 Codex Runtime 目录：" + Utf8ToWide(ec.message());
        return false;
    }
    codexHome = home.wstring();
    std::wofstream stream(home / L"config.toml", std::ios::trunc);
    if (!stream) {
        error = L"无法写入 Codex Runtime 配置";
        return false;
    }
    stream << L"model_provider = \"turingdesk\"\n"
           << L"approval_policy = \"never\"\n"
           << L"sandbox_mode = \"read-only\"\n\n"
           << L"[model_providers.turingdesk]\n"
           << L"name = \"TuringDesk Responses Provider\"\n"
           << L"base_url = \"" << TomlEscape(setup.baseUrl) << L"\"\n"
           << L"env_key = \"TURINGDESK_MODEL_API_KEY\"\n"
           << L"wire_api = \"responses\"\n"
           << L"requires_openai_auth = false\n";
    if (!stream) {
        error = L"Codex Runtime 配置写入失败";
        return false;
    }
    return true;
}

bool CodexRuntime::LaunchProcess(const ProviderSetup& setup, std::wstring& error) {
    bool cliBinary = false;
    const auto binary = FindBinary(cliBinary);
    if (binary.empty()) {
        error = L"未找到 codex-app-server.exe";
        return false;
    }

    std::wstring codexHome;
    if (!ConfigureCodexHome(setup, codexHome, error)) return false;

    SECURITY_ATTRIBUTES security{};
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;

    HANDLE childStdoutRead = nullptr;
    HANDLE childStdoutWrite = nullptr;
    HANDLE childStdinRead = nullptr;
    HANDLE childStdinWrite = nullptr;
    if (!CreatePipe(&childStdoutRead, &childStdoutWrite, &security, 0) ||
        !CreatePipe(&childStdinRead, &childStdinWrite, &security, 0)) {
        error = L"创建 Codex stdio 管道失败";
        if (childStdoutRead) CloseHandle(childStdoutRead);
        if (childStdoutWrite) CloseHandle(childStdoutWrite);
        if (childStdinRead) CloseHandle(childStdinRead);
        if (childStdinWrite) CloseHandle(childStdinWrite);
        return false;
    }
    SetHandleInformation(childStdoutRead, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(childStdinWrite, HANDLE_FLAG_INHERIT, 0);

    HANDLE nullError = CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = childStdinRead;
    startup.hStdOutput = childStdoutWrite;
    startup.hStdError = nullError == INVALID_HANDLE_VALUE ? childStdoutWrite : nullError;

    PROCESS_INFORMATION process{};
    std::wstring commandLine = L"\"" + binary + L"\"";
    if (cliBinary) commandLine += L" app-server --stdio";
    else commandLine += L" --stdio";

    auto environment = BuildEnvironmentBlock(codexHome, setup.apiKey);
    const auto cwd = DesktopDirectory();
    const BOOL created = CreateProcessW(binary.c_str(), commandLine.data(), nullptr, nullptr, TRUE,
                                        CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                                        environment.data(), cwd.empty() ? nullptr : cwd.c_str(),
                                        &startup, &process);

    CloseHandle(childStdinRead);
    CloseHandle(childStdoutWrite);
    if (nullError && nullError != INVALID_HANDLE_VALUE) CloseHandle(nullError);

    if (!created) {
        const DWORD code = GetLastError();
        CloseHandle(childStdoutRead);
        CloseHandle(childStdinWrite);
        error = L"启动 Codex app-server 失败，Win32=" + std::to_wstring(code);
        return false;
    }

    {
        std::scoped_lock lock(processMutex_);
        process_ = process.hProcess;
        processThread_ = process.hThread;
        inputWrite_ = childStdinWrite;
        outputRead_ = childStdoutRead;
    }
    readBuffer_.clear();
    threadId_.clear();
    nextRequestId_ = 1;

    const long long initializeId = nextRequestId_++;
    const std::string initialize =
        "{\"id\":" + std::to_string(initializeId) +
        ",\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"turingdesk\",\"title\":\"TuringDesk L3\",\"version\":\"0.1\"},\"capabilities\":{\"experimentalApi\":true}}}";
    if (!WriteLine(initialize)) {
        error = L"Codex initialize 写入失败";
        CleanupProcess();
        return false;
    }
    std::string response;
    if (!WaitForResponse(initializeId, response, error)) {
        CleanupProcess();
        return false;
    }
    if (!WriteLine("{\"method\":\"initialized\"}")) {
        error = L"Codex initialized 写入失败";
        CleanupProcess();
        return false;
    }

    const long long threadIdRequest = nextRequestId_++;
    const std::wstring developerInstructions =
        L"You are TuringDesk Native Agent. Use the provided native dynamic tools when the user asks to create, open, or inspect supported desktop artifacts. "
        L"Do not claim you cannot access the desktop when a matching tool exists. Never report an action as successful until its tool result reports success. "
        L"Prefer native tools over shell commands for supported tasks.";
    const std::string threadStart =
        "{\"id\":" + std::to_string(threadIdRequest) +
        ",\"method\":\"thread/start\",\"params\":{\"model\":\"" + EscapeJson(setup.model) +
        "\",\"modelProvider\":\"turingdesk\",\"cwd\":\"" + EscapeJson(DesktopDirectory()) +
        "\",\"developerInstructions\":\"" + EscapeJson(developerInstructions) +
        "\",\"dynamicTools\":" + NativeToolDefinitionsJson() +
        ",\"ephemeral\":true}}";
    if (!WriteLine(threadStart) || !WaitForResponse(threadIdRequest, response, error)) {
        CleanupProcess();
        return false;
    }
    const auto threadPos = response.find("\"thread\"");
    const auto id = ExtractJsonString(threadPos == std::string::npos ? std::string_view(response) : std::string_view(response).substr(threadPos), "\"id\"");
    threadId_ = Utf8ToWide(id);
    if (threadId_.empty()) {
        error = L"Codex thread/start 没有返回 thread id";
        CleanupProcess();
        return false;
    }
    sessionSignature_ = setup.signature;
    return true;
}

bool CodexRuntime::EnsureSession(const ProviderSetup& setup, std::wstring& error) {
    {
        std::scoped_lock lock(processMutex_);
        if (process_ && sessionSignature_ == setup.signature && !threadId_.empty()) return true;
    }
    CleanupProcess();
    return LaunchProcess(setup, error);
}

bool CodexRuntime::WriteLine(const std::string& line) {
    HANDLE handle = nullptr;
    {
        std::scoped_lock lock(processMutex_);
        handle = inputWrite_;
    }
    if (!handle) return false;
    std::string payload = line;
    payload.push_back('\n');
    const char* cursor = payload.data();
    DWORD remaining = static_cast<DWORD>(payload.size());
    while (remaining > 0) {
        DWORD written = 0;
        if (!WriteFile(handle, cursor, remaining, &written, nullptr) || written == 0) return false;
        cursor += written;
        remaining -= written;
    }
    return true;
}

bool CodexRuntime::ReadLine(std::string& line) {
    for (;;) {
        const auto newline = readBuffer_.find('\n');
        if (newline != std::string::npos) {
            line = readBuffer_.substr(0, newline);
            readBuffer_.erase(0, newline + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            return true;
        }

        HANDLE handle = nullptr;
        {
            std::scoped_lock lock(processMutex_);
            handle = outputRead_;
        }
        if (!handle) return false;
        char buffer[4096];
        DWORD read = 0;
        if (!ReadFile(handle, buffer, static_cast<DWORD>(sizeof(buffer)), &read, nullptr) || read == 0) return false;
        readBuffer_.append(buffer, read);
        if (readBuffer_.size() > 4 * 1024 * 1024) return false;
    }
}

bool CodexRuntime::WaitForResponse(long long id, std::string& response, std::wstring& error) {
    std::string line;
    while (ReadLine(line)) {
        if (!HasResponseId(line, id)) continue;
        response = line;
        if (line.find("\"result\"") == std::string::npos && line.find("\"error\"") != std::string::npos) {
            auto message = ExtractJsonString(line, "\"message\"");
            error = message.empty() ? L"Codex JSON-RPC 请求失败" : Utf8ToWide(message);
            return false;
        }
        return true;
    }
    error = L"Codex app-server 连接已断开";
    return false;
}

void CodexRuntime::AskAsync(const L3Agent& agent, std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone) {
    if (busy_.exchange(true)) {
        if (onDone) onDone(L"Codex Runtime 正在处理上一条请求");
        return;
    }
    if (worker_.joinable()) worker_.join();
    const auto setup = BuildProviderSetup(agent);
    if (!setup.ok) {
        busy_.store(false);
        if (onDone) onDone(setup.message);
        return;
    }
    worker_ = std::jthread([this, setup, prompt = std::move(prompt), onDelta = std::move(onDelta), onDone = std::move(onDone)](std::stop_token stopToken) mutable {
        RunTurn(setup, std::move(prompt), std::move(onDelta), std::move(onDone), stopToken);
        busy_.store(false);
    });
}

void CodexRuntime::RunTurn(ProviderSetup setup, std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone, std::stop_token stopToken) {
    std::wstring error;
    if (!EnsureSession(setup, error)) {
        if (onDone) onDone(L"Codex Runtime 启动失败：" + error);
        return;
    }

    const long long requestId = nextRequestId_++;
    const std::string request =
        "{\"id\":" + std::to_string(requestId) +
        ",\"method\":\"turn/start\",\"params\":{\"threadId\":\"" + EscapeJson(threadId_) +
        "\",\"input\":[{\"type\":\"text\",\"text\":\"" + EscapeJson(prompt) + "\"}]}}";
    if (!WriteLine(request)) {
        if (onDone) onDone(L"Codex turn/start 写入失败");
        CleanupProcess();
        return;
    }

    bool startAccepted = false;
    std::string line;
    while (!stopToken.stop_requested() && ReadLine(line)) {
        if (HasResponseId(line, requestId)) {
            if (line.find("\"result\"") == std::string::npos && line.find("\"error\"") != std::string::npos) {
                const auto message = ExtractJsonString(line, "\"message\"");
                if (onDone) onDone(message.empty() ? L"Codex turn/start 失败" : Utf8ToWide(message));
                return;
            }
            startAccepted = true;
            continue;
        }
        const auto method = ExtractJsonString(line, "\"method\"");
        if (method == "item/tool/call") {
            long long serverRequestId = 0;
            const auto tool = ExtractJsonString(line, "\"tool\"");
            NativeToolResult result;
            if (!TryReadRequestId(line, serverRequestId)) {
                result = {false, L"TuringDesk 无法解析 Codex tool request id。"};
            } else if (tool.empty()) {
                result = {false, L"Codex tool request 缺少 tool 名称。"};
            } else {
                result = ExecuteNativeTool(tool, line);
            }
            if (serverRequestId != 0) {
                const std::string reply =
                    "{\"id\":" + std::to_string(serverRequestId) +
                    ",\"result\":{\"contentItems\":[{\"type\":\"inputText\",\"text\":\"" + EscapeJson(result.message) +
                    "\"}],\"success\":" + (result.success ? "true" : "false") + "}}";
                if (!WriteLine(reply)) {
                    if (onDone) onDone(L"Codex Native Tool 结果回传失败");
                    CleanupProcess();
                    return;
                }
            }
            continue;
        }
        if (method == "item/agentMessage/delta") {
            const auto delta = ExtractJsonString(line, "\"delta\"");
            if (!delta.empty() && onDelta) onDelta(Utf8ToWide(delta));
            continue;
        }
        if (method == "turn/completed") {
            std::wstring done;
            const auto status = ExtractJsonString(line, "\"status\"");
            if (status == "failed") {
                const auto message = ExtractJsonString(line, "\"message\"");
                done = message.empty() ? L"Codex turn 失败" : Utf8ToWide(message);
            } else if (status == "interrupted") {
                done = L"Codex turn 已中断";
            }
            if (onDone) onDone(std::move(done));
            return;
        }
    }

    if (stopToken.stop_requested()) {
        if (onDone) onDone(L"已取消 Codex 请求");
    } else if (onDone) {
        onDone(startAccepted ? L"Codex app-server 意外断开" : L"Codex turn 未启动");
    }
    CleanupProcess();
}

void CodexRuntime::Stop() {
    if (worker_.joinable()) worker_.request_stop();
    HANDLE process = nullptr;
    {
        std::scoped_lock lock(processMutex_);
        process = process_;
    }
    if (process) TerminateProcess(process, 0);
}

void CodexRuntime::ResetSession() {
    Stop();
    if (worker_.joinable()) worker_.join();
    busy_.store(false);
    CleanupProcess();
}

void CodexRuntime::CleanupProcess() {
    std::scoped_lock lock(processMutex_);
    if (inputWrite_) { CloseHandle(inputWrite_); inputWrite_ = nullptr; }
    if (outputRead_) { CloseHandle(outputRead_); outputRead_ = nullptr; }
    if (processThread_) { CloseHandle(processThread_); processThread_ = nullptr; }
    if (process_) {
        const DWORD wait = WaitForSingleObject(process_, 0);
        if (wait == WAIT_TIMEOUT) TerminateProcess(process_, 0);
        CloseHandle(process_);
        process_ = nullptr;
    }
    readBuffer_.clear();
    threadId_.clear();
    sessionSignature_.clear();
}

} // namespace turingdesk
