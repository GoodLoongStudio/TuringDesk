#include "turingdesk/HarnessProcessManager.h"
#include "turingdesk/HarnessSettingsBridge.h"
#include <winhttp.h>
#include <algorithm>
#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kHarnessHost[] = L"127.0.0.1";
constexpr INTERNET_PORT kHarnessPort = 3080;
constexpr wchar_t kHarnessPath[] = L"/";
constexpr wchar_t kHarnessArgs[] = L"web --no-open --host 127.0.0.1 --port 3080";
constexpr char kHarnessBootMarker[] = "window.__DSH_BOOT__";
constexpr std::size_t kMaxReadinessProbeBytes = 256 * 1024;

struct LaunchSpec {
    std::wstring application;
    std::wstring commandLine;
    std::wstring mode;
    bool Valid() const { return !application.empty() && !commandLine.empty(); }
};

std::wstring Win32ErrorText(DWORD error) {
    if (error == ERROR_SUCCESS) return {};
    wchar_t* buffer = nullptr;
    const DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS;
    const DWORD length = FormatMessageW(flags, nullptr, error, 0,
                                        reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::wstring text;
    if (length && buffer) {
        text.assign(buffer, length);
        while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n' || text.back() == L' ')) text.pop_back();
    } else {
        text = L"Win32=" + std::to_wstring(error);
    }
    if (buffer) LocalFree(buffer);
    return text;
}

fs::path LocalAppDataDirectory() {
    wchar_t value[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", value, static_cast<DWORD>(std::size(value)));
    if (length > 0 && length < std::size(value)) return fs::path(std::wstring(value, length));

    wchar_t tempPath[32768]{};
    const DWORD tempLength = GetTempPathW(static_cast<DWORD>(std::size(tempPath)), tempPath);
    if (tempLength > 0 && tempLength < std::size(tempPath)) return fs::path(std::wstring(tempPath, tempLength));
    return {};
}

fs::path HarnessLogPathFs() {
    const fs::path root = LocalAppDataDirectory();
    if (root.empty()) return {};
    const fs::path directory = root / L"TuringDesk" / L"Logs";
    std::error_code ec;
    fs::create_directories(directory, ec);
    if (ec) return {};
    return directory / L"harness.log";
}

std::wstring UserHomeDirectory() {
    wchar_t value[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"USERPROFILE", value, static_cast<DWORD>(std::size(value)));
    if (length > 0 && length < std::size(value)) return value;
    return {};
}

bool IsRegularFile(const fs::path& path) {
    std::error_code ec;
    return !path.empty() && fs::is_regular_file(path, ec);
}

fs::path ExecutableDirectory() {
    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    return fs::path(std::wstring(buffer.data(), length)).parent_path();
}

std::wstring Quote(const std::wstring& value) {
    return L"\"" + value + L"\"";
}

class ScopedEnvironmentOverride {
public:
    ScopedEnvironmentOverride(const wchar_t* name, const std::wstring& value) : name_(name ? name : L"") {
        if (name_.empty() || value.empty()) return;
        const DWORD needed = GetEnvironmentVariableW(name_.c_str(), nullptr, 0);
        if (needed > 0) {
            previous_.resize(needed);
            const DWORD written = GetEnvironmentVariableW(name_.c_str(), previous_.data(), needed);
            if (written > 0 && written < needed) {
                previous_.resize(written);
                hadPrevious_ = true;
            } else {
                previous_.clear();
            }
        }
        active_ = SetEnvironmentVariableW(name_.c_str(), value.c_str()) != FALSE;
    }

    ~ScopedEnvironmentOverride() {
        if (!active_) return;
        SetEnvironmentVariableW(name_.c_str(), hadPrevious_ ? previous_.c_str() : nullptr);
    }

    ScopedEnvironmentOverride(const ScopedEnvironmentOverride&) = delete;
    ScopedEnvironmentOverride& operator=(const ScopedEnvironmentOverride&) = delete;

private:
    std::wstring name_;
    std::wstring previous_;
    bool hadPrevious_{};
    bool active_{};
};

std::wstring BuildDirectDshCommand(const std::wstring& nodePath, const std::wstring& dshBin) {
    return Quote(nodePath) + L" " + Quote(dshBin) + L" " + kHarnessArgs;
}

LaunchSpec ResolveLaunchSpec() {
    const fs::path appDir = ExecutableDirectory();
    if (appDir.empty()) return {};

    const fs::path runtimeRoot = appDir / L"Runtime" / L"Node";
    const fs::path node = runtimeRoot / L"node.exe";
    const fs::path dshBin = runtimeRoot / L"node_modules" / L"@deepseek-ai" / L"dsh" / L"lib" / L"bin.js";
    if (!IsRegularFile(node) || !IsRegularFile(dshBin)) return {};

    return {node.wstring(), BuildDirectDshCommand(node.wstring(), dshBin.wstring()),
            L"repository-vendored official DeepSeek Harness"};
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                          nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string text(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        text.data(), count, nullptr, nullptr);
    return text;
}

void WriteLogLine(HANDLE handle, const std::wstring& line) {
    if (!handle || handle == INVALID_HANDLE_VALUE) return;
    const std::string utf8 = WideToUtf8(line + L"\r\n");
    if (utf8.empty()) return;
    DWORD written = 0;
    WriteFile(handle, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
}

bool ResponseContainsHarnessBootManifest(HINTERNET request) {
    std::string body;
    body.reserve(16 * 1024);

    while (body.size() < kMaxReadinessProbeBytes) {
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available) || available == 0) break;

        const std::size_t remaining = kMaxReadinessProbeBytes - body.size();
        const DWORD toRead = static_cast<DWORD>(std::min<std::size_t>(available, remaining));
        const std::size_t previousSize = body.size();
        body.resize(previousSize + toRead);

        DWORD bytesRead = 0;
        if (!WinHttpReadData(request, body.data() + previousSize, toRead, &bytesRead)) {
            body.resize(previousSize);
            return false;
        }
        body.resize(previousSize + bytesRead);

        if (body.find(kHarnessBootMarker) != std::string::npos) return true;
        if (bytesRead == 0) break;
    }

    return false;
}

bool ProbeHarnessHttp() {
    HINTERNET session = WinHttpOpen(L"TuringDesk/0.1",
                                    WINHTTP_ACCESS_TYPE_NO_PROXY,
                                    WINHTTP_NO_PROXY_NAME,
                                    WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    WinHttpSetTimeouts(session, 400, 400, 400, 400);

    HINTERNET connection = WinHttpConnect(session, kHarnessHost, kHarnessPort, 0);
    if (!connection) {
        WinHttpCloseHandle(session);
        return false;
    }

    HINTERNET request = WinHttpOpenRequest(connection, L"GET", kHarnessPath,
                                           nullptr, WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES,
                                           WINHTTP_FLAG_REFRESH);
    if (!request) {
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return false;
    }

    bool ready = false;
    if (WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                           WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
        WinHttpReceiveResponse(request, nullptr)) {
        DWORD status = 0;
        DWORD size = sizeof(status);
        if (WinHttpQueryHeaders(request,
                                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                                WINHTTP_HEADER_NAME_BY_INDEX,
                                &status, &size, WINHTTP_NO_HEADER_INDEX) &&
            status == 200) {
            ready = ResponseContainsHarnessBootManifest(request);
        }
    }

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return ready;
}

} // namespace

struct HarnessProcessManager::Impl {
    HANDLE job{};
    HANDLE process{};
    DWORD processId{};
    std::wstring lastError;

    void CloseHandles() {
        if (job) {
            CloseHandle(job);
            job = nullptr;
        }
        if (process) {
            WaitForSingleObject(process, 3000);
            CloseHandle(process);
            process = nullptr;
        }
        processId = 0;
    }
};

HarnessProcessManager::HarnessProcessManager() : impl_(std::make_unique<Impl>()) {}
HarnessProcessManager::~HarnessProcessManager() { Stop(); }

bool HarnessProcessManager::Start() {
    const HarnessSettingsBridgeState bridge = PrepareHarnessSettingsBridge();
    if (!bridge.error.empty()) {
        impl_->lastError = L"无法同步 TuringDesk AI 设置到 DeepSeek Harness：" + bridge.error;
        return false;
    }
    if (Running() || ServiceReady()) return true;
    Stop();
    impl_->lastError.clear();

    const LaunchSpec launch = ResolveLaunchSpec();
    if (!launch.Valid()) {
        impl_->lastError = L"TuringDesk ARM64 RuntimeBundle 不完整：缺少 Runtime\\Node\\node.exe 或 Runtime\\Node\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js。请重新运行 DEPLOY-NATIVE-ARM64.cmd。不会回退到系统 Node/npm。";
        return false;
    }

    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (!job) {
        impl_->lastError = L"无法创建 Harness Job Object：" + Win32ErrorText(GetLastError());
        return false;
    }

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
        impl_->lastError = L"无法配置 Harness Job Object：" + Win32ErrorText(GetLastError());
        CloseHandle(job);
        return false;
    }

    const fs::path logPath = HarnessLogPathFs();
    if (logPath.empty()) {
        impl_->lastError = L"无法创建 Harness 日志目录。";
        CloseHandle(job);
        return false;
    }

    SECURITY_ATTRIBUTES security{};
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;

    HANDLE logHandle = CreateFileW(logPath.c_str(), GENERIC_WRITE,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                   &security, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (logHandle == INVALID_HANDLE_VALUE) {
        impl_->lastError = L"无法创建 Harness 日志：" + Win32ErrorText(GetLastError()) + L" · " + logPath.wstring();
        CloseHandle(job);
        return false;
    }

    WriteLogLine(logHandle, L"[TuringDesk] DeepSeek Harness launch requested");
    WriteLogLine(logHandle, L"[TuringDesk] mode: " + launch.mode);
    WriteLogLine(logHandle, L"[TuringDesk] application: " + launch.application);
    WriteLogLine(logHandle, L"[TuringDesk] command: " + launch.commandLine);
    if (bridge.configured) {
        WriteLogLine(logHandle, L"[TuringDesk] shared model: provider=" + bridge.providerId + L" model=" + bridge.model);
        WriteLogLine(logHandle, L"[TuringDesk] shared base URL: " + bridge.baseUrl);
        WriteLogLine(logHandle, bridge.hasApiKey
            ? L"[TuringDesk] shared API key: injected from Windows Credential Manager"
            : L"[TuringDesk] shared API key: none (local/keyless provider expected)");
    } else {
        WriteLogLine(logHandle, L"[TuringDesk] shared model: TuringDesk AI settings are not configured yet");
    }
    WriteLogLine(logHandle, L"[TuringDesk] external browser: disabled; UI is hosted by TuringDesk WebView2");
    WriteLogLine(logHandle, L"[TuringDesk] network bootstrap: disabled; using repository RuntimeBundle");
    WriteLogLine(logHandle, L"[TuringDesk] waiting for upstream stdout/stderr...");
    FlushFileBuffers(logHandle);

    HANDLE inputHandle = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
                                     FILE_SHARE_READ | FILE_SHARE_WRITE,
                                     &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (inputHandle == INVALID_HANDLE_VALUE) {
        WriteLogLine(logHandle, L"[TuringDesk] ERROR: unable to open NUL stdin: " + Win32ErrorText(GetLastError()));
        impl_->lastError = L"无法初始化 Harness 标准输入：" + Win32ErrorText(GetLastError());
        CloseHandle(logHandle);
        CloseHandle(job);
        return false;
    }

    std::vector<wchar_t> commandBuffer(launch.commandLine.begin(), launch.commandLine.end());
    commandBuffer.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES;
    startup.wShowWindow = SW_HIDE;
    startup.hStdInput = inputHandle;
    startup.hStdOutput = logHandle;
    startup.hStdError = logHandle;

    PROCESS_INFORMATION processInfo{};
    const std::wstring home = UserHomeDirectory();
    const DWORD flags = CREATE_NO_WINDOW | CREATE_SUSPENDED;
    BOOL created = FALSE;
    DWORD createError = ERROR_SUCCESS;
    {
        ScopedEnvironmentOverride dshHome(L"DSH_HOME", bridge.dshHome);
        ScopedEnvironmentOverride apiKey(L"TURINGDESK_API_KEY", bridge.apiKey);
        created = CreateProcessW(launch.application.c_str(), commandBuffer.data(), nullptr, nullptr, TRUE, flags,
                                 nullptr, home.empty() ? nullptr : home.c_str(), &startup, &processInfo);
        createError = created ? ERROR_SUCCESS : GetLastError();
    }
    CloseHandle(inputHandle);

    if (!created) {
        WriteLogLine(logHandle, L"[TuringDesk] ERROR: CreateProcessW failed: " + Win32ErrorText(createError));
        FlushFileBuffers(logHandle);
        CloseHandle(logHandle);
        impl_->lastError = L"无法启动 DeepSeek Harness：" + Win32ErrorText(createError) + L" · 日志：" + logPath.wstring();
        CloseHandle(job);
        return false;
    }

    if (!AssignProcessToJobObject(job, processInfo.hProcess)) {
        WriteLogLine(logHandle, L"[TuringDesk] ERROR: AssignProcessToJobObject failed: " + Win32ErrorText(GetLastError()));
        FlushFileBuffers(logHandle);
        CloseHandle(logHandle);
        impl_->lastError = L"无法接管 Harness 进程树：" + Win32ErrorText(GetLastError()) + L" · 日志：" + logPath.wstring();
        TerminateProcess(processInfo.hProcess, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        CloseHandle(job);
        return false;
    }

    if (ResumeThread(processInfo.hThread) == static_cast<DWORD>(-1)) {
        WriteLogLine(logHandle, L"[TuringDesk] ERROR: ResumeThread failed: " + Win32ErrorText(GetLastError()));
        FlushFileBuffers(logHandle);
        CloseHandle(logHandle);
        impl_->lastError = L"无法恢复 Harness 进程：" + Win32ErrorText(GetLastError()) + L" · 日志：" + logPath.wstring();
        TerminateJobObject(job, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        CloseHandle(job);
        return false;
    }

    WriteLogLine(logHandle, L"[TuringDesk] process started, pid=" + std::to_wstring(processInfo.dwProcessId));
    FlushFileBuffers(logHandle);
    CloseHandle(logHandle);
    CloseHandle(processInfo.hThread);
    impl_->job = job;
    impl_->process = processInfo.hProcess;
    impl_->processId = processInfo.dwProcessId;
    return true;
}

void HarnessProcessManager::Stop() { if (impl_) impl_->CloseHandles(); }

bool HarnessProcessManager::Running() const {
    if (!impl_ || !impl_->process) return false;
    DWORD exitCode = 0;
    return GetExitCodeProcess(impl_->process, &exitCode) && exitCode == STILL_ACTIVE;
}

DWORD HarnessProcessManager::ExitCode() const {
    if (!impl_ || !impl_->process) return STILL_ACTIVE;
    DWORD exitCode = STILL_ACTIVE;
    if (!GetExitCodeProcess(impl_->process, &exitCode)) return STILL_ACTIVE;
    return exitCode;
}

bool HarnessProcessManager::ServiceReady() const { return ProbeHarnessHttp(); }

bool HarnessProcessManager::WaitUntilReady(DWORD timeoutMs) {
    if (ServiceReady()) {
        impl_->lastError.clear();
        return true;
    }
    if (!Running()) {
        const DWORD exitCode = ExitCode();
        if (exitCode != STILL_ACTIVE) {
            impl_->lastError = L"Harness 在 Web UI 就绪前退出，ExitCode=" + std::to_wstring(exitCode) + L" · 日志：" + LogPath();
        } else if (impl_->lastError.empty()) {
            impl_->lastError = L"Harness 未运行";
        }
        return false;
    }

    const ULONGLONG start = GetTickCount64();
    do {
        if (ServiceReady()) {
            impl_->lastError.clear();
            return true;
        }
        if (!Running()) {
            const DWORD exitCode = ExitCode();
            impl_->lastError = L"Harness 在 Web UI 就绪前退出，ExitCode=" + std::to_wstring(exitCode) + L" · 日志：" + LogPath();
            return false;
        }
        Sleep(100);
    } while (GetTickCount64() - start < timeoutMs);

    impl_->lastError = L"等待 DeepSeek 官方 Harness Web UI 超时（" + DefaultUrl() + L"） · 日志：" + LogPath();
    return false;
}

const std::wstring& HarnessProcessManager::LastError() const { return impl_->lastError; }
std::wstring HarnessProcessManager::DefaultUrl() { return L"http://localhost:3080"; }
std::wstring HarnessProcessManager::LogPath() {
    const fs::path path = HarnessLogPathFs();
    return path.empty() ? std::wstring{} : path.wstring();
}

std::wstring HarnessProcessManager::BuildLaunchCommand() {
    const LaunchSpec resolved = ResolveLaunchSpec();
    if (resolved.Valid()) return resolved.commandLine;
    return L"<TuringDesk>\\Runtime\\Node\\node.exe <TuringDesk>\\Runtime\\Node\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js web --no-open --host 127.0.0.1 --port 3080";
}

bool HarnessProcessManager::SelfTest() {
    const std::wstring sampleNode = L"C:\\TuringDesk\\Runtime\\Node\\node.exe";
    const std::wstring sampleDsh = L"C:\\TuringDesk\\Runtime\\Node\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js";
    const std::wstring dshCommand = BuildDirectDshCommand(sampleNode, sampleDsh);

    const auto bindsOnlyLoopback = [](const std::wstring& command) {
        return command.find(L"--host 127.0.0.1") != std::wstring::npos &&
               command.find(L"--port 3080") != std::wstring::npos &&
               command.find(L"--host 0.0.0.0") == std::wstring::npos &&
               command.find(L"--host ::") == std::wstring::npos;
    };

    return HarnessSettingsBridgeSelfTest() &&
           DefaultUrl() == L"http://localhost:3080" &&
           dshCommand.find(L"Runtime\\Node\\node.exe") != std::wstring::npos &&
           dshCommand.find(L"@deepseek-ai\\dsh\\lib\\bin.js") != std::wstring::npos &&
           dshCommand.find(L"npx") == std::wstring::npos &&
           dshCommand.find(L"registry.npmjs.org") == std::wstring::npos &&
           dshCommand.find(L"--no-open") != std::wstring::npos &&
           bindsOnlyLoopback(dshCommand) &&
           BuildLaunchCommand().find(L"npx") == std::wstring::npos &&
           std::string_view(kHarnessBootMarker) == "window.__DSH_BOOT__" &&
           !LogPath().empty();
}

} // namespace turingdesk
