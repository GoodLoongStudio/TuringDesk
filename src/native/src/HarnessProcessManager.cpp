#include "turingdesk/HarnessProcessManager.h"
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

// Keep service probing on the literal loopback address so readiness checks never
// involve name resolution or proxy configuration. The browser-facing URL uses
// localhost instead (see DefaultUrl) to avoid the current Harness Chromium
// Origin/Host trust regression on 127.0.0.1:3080.
constexpr wchar_t kHarnessHost[] = L"127.0.0.1";
constexpr INTERNET_PORT kHarnessPort = 3080;
constexpr wchar_t kHarnessPath[] = L"/";
constexpr wchar_t kHarnessPackage[] = L"@deepseek-ai/dsh@0.1.0-rc.7";
constexpr wchar_t kBundledNpxRelativePath[] = L"HarnessRuntime\\Node\\npx.cmd";
constexpr char kHarnessBootMarker[] = "window.__DSH_BOOT__";
constexpr std::size_t kMaxReadinessProbeBytes = 256 * 1024;

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

std::wstring UserHomeDirectory() {
    wchar_t value[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"USERPROFILE", value, static_cast<DWORD>(std::size(value)));
    if (length > 0 && length < std::size(value)) return value;
    return {};
}

std::wstring ResolveNpxPath() {
    const fs::path moduleDirectory = ModuleDirectory();
    if (!moduleDirectory.empty()) {
        const fs::path bundled = moduleDirectory / kBundledNpxRelativePath;
        std::error_code ec;
        if (fs::is_regular_file(bundled, ec)) return bundled.wstring();
    }

    wchar_t path[32768]{};
    const DWORD length = SearchPathW(nullptr, L"npx.cmd", nullptr,
                                     static_cast<DWORD>(std::size(path)), path, nullptr);
    if (length > 0 && length < std::size(path)) return std::wstring(path, length);
    return {};
}

std::wstring BuildLaunchCommandFor(const std::wstring& npxPath) {
    // cmd /s /c requires the doubled opening quote when the command itself starts
    // with a quoted executable path. This keeps portable package paths with spaces safe.
    return L"cmd.exe /d /s /c \"\"" + npxPath + L"\" --yes " + kHarnessPackage + L" web\"";
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

    const std::wstring npxPath = ResolveNpxPath();
    if (npxPath.empty()) {
        impl_->lastError = L"未找到 Harness 运行时。请使用包含 HarnessRuntime\\Node 的完整 TuringDesk 安装包。";
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

    const std::wstring comspec = ComSpecPath();
    std::wstring command = BuildLaunchCommandFor(npxPath);
    std::vector<wchar_t> commandBuffer(command.begin(), command.end());
    commandBuffer.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION processInfo{};
    const std::wstring home = UserHomeDirectory();
    const DWORD flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED;
    if (!CreateProcessW(comspec.c_str(), commandBuffer.data(), nullptr, nullptr, FALSE, flags,
                        nullptr, home.empty() ? nullptr : home.c_str(), &startup, &processInfo)) {
        impl_->lastError = L"无法启动 DeepSeek Harness：" + Win32ErrorText(GetLastError());
        CloseHandle(job);
        return false;
    }

    if (!AssignProcessToJobObject(job, processInfo.hProcess)) {
        impl_->lastError = L"无法接管 Harness 进程树：" + Win32ErrorText(GetLastError());
        TerminateProcess(processInfo.hProcess, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        CloseHandle(job);
        return false;
    }

    if (ResumeThread(processInfo.hThread) == static_cast<DWORD>(-1)) {
        impl_->lastError = L"无法恢复 Harness 进程：" + Win32ErrorText(GetLastError());
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
            impl_->lastError = L"Harness 在 Web UI 就绪前退出，ExitCode=" + std::to_wstring(exitCode);
            return false;
        }
        Sleep(100);
    } while (GetTickCount64() - start < timeoutMs);

    impl_->lastError = L"等待 Harness Web UI 超时（" + DefaultUrl() + L"）";
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

std::wstring HarnessProcessManager::BuildLaunchCommand() {
    std::wstring npxPath = ResolveNpxPath();
    if (npxPath.empty()) npxPath = L"npx.cmd";
    return BuildLaunchCommandFor(npxPath);
}

bool HarnessProcessManager::SelfTest() {
    const auto command = BuildLaunchCommand();
    return DefaultUrl() == L"http://localhost:3080" &&
           command.find(kHarnessPackage) != std::wstring::npos &&
           command.find(L" web") != std::wstring::npos &&
           command.find(L"/d /s /c") != std::wstring::npos &&
           std::string_view(kHarnessBootMarker) == "window.__DSH_BOOT__";
}

} // namespace turingdesk
