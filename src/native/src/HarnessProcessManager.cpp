#include "turingdesk/HarnessProcessManager.h"
#include <winhttp.h>
#include <algorithm>
#include <cwchar>
#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

// Keep service probing on the literal loopback address so readiness checks never
// involve name resolution or proxy configuration. The browser-facing URL uses
// localhost instead (see DefaultUrl) to avoid the current Harness Chromium
// Origin/Host trust regression on 127.0.0.1:3080.
constexpr wchar_t kHarnessHost[] = L"127.0.0.1";
constexpr INTERNET_PORT kHarnessPort = 3080;
constexpr wchar_t kHarnessPath[] = L"/";
constexpr wchar_t kHarnessPackage[] = L"@deepseek-ai/dsh@0.1.0-rc.7";
constexpr wchar_t kBundledNodeRelativePath[] = L"HarnessRuntime\\Node\\node.exe";
constexpr wchar_t kBundledNpxRelativePath[] = L"HarnessRuntime\\Node\\npx.cmd";
constexpr wchar_t kBundledDshBinRelativePath[] = L"HarnessRuntime\\Dsh\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js";
constexpr char kHarnessBootMarker[] = "window.__DSH_BOOT__";
constexpr std::size_t kMaxReadinessProbeBytes = 256 * 1024;

struct LaunchSpec {
    std::wstring application;
    std::wstring commandLine;
    fs::path bundledNodeDirectory;

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

std::wstring ComSpecPath() {
    wchar_t value[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"ComSpec", value, static_cast<DWORD>(std::size(value)));
    if (length > 0 && length < std::size(value)) return value;

    wchar_t systemDir[MAX_PATH]{};
    const UINT systemLength = GetSystemDirectoryW(systemDir, static_cast<UINT>(std::size(systemDir)));
    if (systemLength > 0 && systemLength < std::size(systemDir))
        return std::wstring(systemDir) + L"\\cmd.exe";
    return L"cmd.exe";
}

fs::path ModuleDirectory() {
    wchar_t value[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, value, static_cast<DWORD>(std::size(value)));
    if (length == 0 || length >= std::size(value)) return {};
    return fs::path(std::wstring(value, length)).parent_path();
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

fs::path BundledNodeDirectory() {
    const fs::path moduleDirectory = ModuleDirectory();
    if (moduleDirectory.empty()) return {};
    const fs::path node = moduleDirectory / kBundledNodeRelativePath;
    return IsRegularFile(node) ? node.parent_path() : fs::path{};
}

std::wstring ResolveNpxPath() {
    const fs::path moduleDirectory = ModuleDirectory();
    if (!moduleDirectory.empty()) {
        const fs::path bundled = moduleDirectory / kBundledNpxRelativePath;
        if (IsRegularFile(bundled)) return bundled.wstring();
    }

    wchar_t path[32768]{};
    const DWORD length = SearchPathW(nullptr, L"npx.cmd", nullptr,
                                     static_cast<DWORD>(std::size(path)), path, nullptr);
    if (length > 0 && length < std::size(path)) return std::wstring(path, length);
    return {};
}

std::wstring BuildNpxLaunchCommandFor(const std::wstring& npxPath) {
    // cmd /s /c requires the doubled opening quote when the command itself starts
    // with a quoted executable path. This keeps portable package paths with spaces safe.
    return L"cmd.exe /d /s /c \"\"" + npxPath + L"\" --yes " + kHarnessPackage + L" web\"";
}

LaunchSpec ResolveLaunchSpec() {
    const fs::path moduleDirectory = ModuleDirectory();
    if (!moduleDirectory.empty()) {
        const fs::path node = moduleDirectory / kBundledNodeRelativePath;
        const fs::path dshBin = moduleDirectory / kBundledDshBinRelativePath;
        if (IsRegularFile(node) && IsRegularFile(dshBin)) {
            LaunchSpec spec;
            spec.application = node.wstring();
            spec.commandLine = L"\"" + node.wstring() + L"\" \"" + dshBin.wstring() + L"\" web";
            spec.bundledNodeDirectory = node.parent_path();
            return spec;
        }
    }

    const std::wstring npx = ResolveNpxPath();
    if (npx.empty()) return {};

    LaunchSpec spec;
    spec.application = ComSpecPath();
    spec.commandLine = BuildNpxLaunchCommandFor(npx);
    spec.bundledNodeDirectory = BundledNodeDirectory();
    return spec;
}

std::vector<wchar_t> BuildChildEnvironment(const fs::path& bundledNodeDirectory) {
    if (bundledNodeDirectory.empty()) return {};

    LPWCH environment = GetEnvironmentStringsW();
    if (!environment) return {};

    std::vector<std::wstring> entries;
    std::wstring existingPath;
    for (const wchar_t* cursor = environment; *cursor != L'\0'; cursor += std::wcslen(cursor) + 1) {
        std::wstring entry(cursor);
        const std::size_t equals = entry.find(L'=', entry.starts_with(L'=') ? 1 : 0);
        if (equals != std::wstring::npos) {
            const std::wstring name = entry.substr(0, equals);
            if (_wcsicmp(name.c_str(), L"PATH") == 0) {
                existingPath = entry.substr(equals + 1);
                continue;
            }
        }
        entries.push_back(std::move(entry));
    }
    FreeEnvironmentStringsW(environment);

    std::wstring pathEntry = L"PATH=" + bundledNodeDirectory.wstring();
    if (!existingPath.empty()) pathEntry += L";" + existingPath;
    entries.push_back(std::move(pathEntry));

    std::sort(entries.begin(), entries.end(), [](const std::wstring& left, const std::wstring& right) {
        return _wcsicmp(left.c_str(), right.c_str()) < 0;
    });

    std::vector<wchar_t> block;
    std::size_t characters = 1;
    for (const auto& entry : entries) characters += entry.size() + 1;
    block.reserve(characters);
    for (const auto& entry : entries) {
        block.insert(block.end(), entry.begin(), entry.end());
        block.push_back(L'\0');
    }
    block.push_back(L'\0');
    return block;
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
            // A bare 200 on port 3080 is not sufficient: another process could
            // own the port, or Harness may have returned the shell before its
            // production boot payload is composed. Official dsh web injects
            // window.__DSH_BOOT__ into the index when the Web UI is truly ready.
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

HarnessProcessManager::~HarnessProcessManager() {
    Stop();
}

bool HarnessProcessManager::Start() {
    if (Running() || ServiceReady()) return true;
    Stop();
    impl_->lastError.clear();

    const LaunchSpec launch = ResolveLaunchSpec();
    if (!launch.Valid()) {
        impl_->lastError = L"未找到 Harness 运行时。完整安装包应包含 HarnessRuntime\\Node 与 HarnessRuntime\\Dsh。";
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

    HANDLE inputHandle = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
                                     FILE_SHARE_READ | FILE_SHARE_WRITE,
                                     &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (inputHandle == INVALID_HANDLE_VALUE) {
        impl_->lastError = L"无法初始化 Harness 标准输入：" + Win32ErrorText(GetLastError());
        CloseHandle(logHandle);
        CloseHandle(job);
        return false;
    }

    std::vector<wchar_t> commandBuffer(launch.commandLine.begin(), launch.commandLine.end());
    commandBuffer.push_back(L'\0');
    std::vector<wchar_t> environmentBlock = BuildChildEnvironment(launch.bundledNodeDirectory);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES;
    startup.wShowWindow = SW_HIDE;
    startup.hStdInput = inputHandle;
    startup.hStdOutput = logHandle;
    startup.hStdError = logHandle;

    PROCESS_INFORMATION processInfo{};
    const std::wstring home = UserHomeDirectory();
    const DWORD flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED;
    LPVOID childEnvironment = environmentBlock.empty() ? nullptr : environmentBlock.data();
    const BOOL created = CreateProcessW(launch.application.c_str(), commandBuffer.data(), nullptr, nullptr, TRUE, flags,
                                        childEnvironment, home.empty() ? nullptr : home.c_str(), &startup, &processInfo);
    const DWORD createError = created ? ERROR_SUCCESS : GetLastError();
    CloseHandle(inputHandle);
    CloseHandle(logHandle);

    if (!created) {
        impl_->lastError = L"无法启动 DeepSeek Harness：" + Win32ErrorText(createError) +
                           L" · 日志：" + logPath.wstring();
        CloseHandle(job);
        return false;
    }

    if (!AssignProcessToJobObject(job, processInfo.hProcess)) {
        impl_->lastError = L"无法接管 Harness 进程树：" + Win32ErrorText(GetLastError()) +
                           L" · 日志：" + logPath.wstring();
        TerminateProcess(processInfo.hProcess, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        CloseHandle(job);
        return false;
    }

    if (ResumeThread(processInfo.hThread) == static_cast<DWORD>(-1)) {
        impl_->lastError = L"无法恢复 Harness 进程：" + Win32ErrorText(GetLastError()) +
                           L" · 日志：" + logPath.wstring();
        TerminateJobObject(job, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        CloseHandle(job);
        return false;
    }

    CloseHandle(processInfo.hThread);
    impl_->job = job;
    impl_->process = processInfo.hProcess;
    impl_->processId = processInfo.dwProcessId;
    return true;
}

void HarnessProcessManager::Stop() {
    if (!impl_) return;
    impl_->CloseHandles();
}

bool HarnessProcessManager::Running() const {
    if (!impl_ || !impl_->process) return false;
    DWORD exitCode = 0;
    return GetExitCodeProcess(impl_->process, &exitCode) && exitCode == STILL_ACTIVE;
}

bool HarnessProcessManager::ServiceReady() const {
    return ProbeHarnessHttp();
}

bool HarnessProcessManager::WaitUntilReady(DWORD timeoutMs) {
    if (ServiceReady()) {
        impl_->lastError.clear();
        return true;
    }
    if (!Running()) {
        if (impl_->lastError.empty()) impl_->lastError = L"Harness 未运行";
        return false;
    }

    const ULONGLONG start = GetTickCount64();
    do {
        if (ServiceReady()) {
            impl_->lastError.clear();
            return true;
        }
        if (!Running()) {
            DWORD exitCode = 0;
            GetExitCodeProcess(impl_->process, &exitCode);
            impl_->lastError = L"Harness 在 Web UI 就绪前退出，ExitCode=" + std::to_wstring(exitCode) +
                               L" · 日志：" + LogPath();
            return false;
        }
        Sleep(100);
    } while (GetTickCount64() - start < timeoutMs);

    impl_->lastError = L"等待 Harness Web UI 超时（" + DefaultUrl() + L"） · 日志：" + LogPath();
    return false;
}

const std::wstring& HarnessProcessManager::LastError() const {
    return impl_->lastError;
}

std::wstring HarnessProcessManager::DefaultUrl() {
    // Harness currently has a Chromium 151 Origin/Host regression when opened
    // through the literal 127.0.0.1 authority. localhost resolves to the same
    // loopback listener while preserving the browser authority expected by its
    // browser-trust fence.
    return L"http://localhost:3080";
}

std::wstring HarnessProcessManager::LogPath() {
    const fs::path path = HarnessLogPathFs();
    return path.empty() ? std::wstring{} : path.wstring();
}

std::wstring HarnessProcessManager::BuildLaunchCommand() {
    const LaunchSpec resolved = ResolveLaunchSpec();
    if (resolved.Valid()) return resolved.commandLine;
    return BuildNpxLaunchCommandFor(L"npx.cmd");
}

bool HarnessProcessManager::SelfTest() {
    const std::wstring directNode = L"C:\\Program Files\\TuringDesk\\HarnessRuntime\\Node\\node.exe";
    const std::wstring directBin = L"C:\\Program Files\\TuringDesk\\HarnessRuntime\\Dsh\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js";
    const std::wstring directCommand = L"\"" + directNode + L"\" \"" + directBin + L"\" web";
    const std::wstring npxCommand = BuildNpxLaunchCommandFor(L"C:\\Program Files\\TuringDesk\\HarnessRuntime\\Node\\npx.cmd");

    return DefaultUrl() == L"http://localhost:3080" &&
           directCommand.find(L"node.exe\"") != std::wstring::npos &&
           directCommand.find(L"@deepseek-ai\\dsh\\lib\\bin.js") != std::wstring::npos &&
           directCommand.ends_with(L" web") &&
           npxCommand.find(kHarnessPackage) != std::wstring::npos &&
           npxCommand.find(L"/d /s /c") != std::wstring::npos &&
           std::string_view(kHarnessBootMarker) == "window.__DSH_BOOT__" &&
           !LogPath().empty();
}

} // namespace turingdesk
