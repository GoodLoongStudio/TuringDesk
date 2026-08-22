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

constexpr wchar_t kHarnessHost[] = L"127.0.0.1";
constexpr INTERNET_PORT kHarnessPort = 3080;
constexpr wchar_t kHarnessPath[] = L"/";
constexpr wchar_t kHarnessPackage[] = L"@deepseek-ai/dsh";
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

std::wstring ResolvePath(const wchar_t* fileName) {
    wchar_t path[32768]{};
    const DWORD length = SearchPathW(nullptr, fileName, nullptr,
                                     static_cast<DWORD>(std::size(path)), path, nullptr);
    if (length > 0 && length < std::size(path)) return std::wstring(path, length);
    return {};
}

std::wstring Quote(const std::wstring& value) {
    return L"\"" + value + L"\"";
}

std::wstring BuildDirectDshCommand(const std::wstring& nodePath, const std::wstring& dshBin) {
    return Quote(nodePath) + L" " + Quote(dshBin) + L" " + kHarnessArgs;
}

std::wstring BuildDirectNpxCommand(const std::wstring& nodePath, const std::wstring& npxCli) {
    // Keep DeepSeek's official README flow while making npm's Windows network
    // path deterministic. Chromium performs aggressive IPv4/IPv6 fallback;
    // Node can otherwise spend a long time on a poor IPv6 route even when the
    // same registry opens instantly in a browser.
    return Quote(nodePath) + L" --dns-result-order=ipv4first " + Quote(npxCli) +
           L" --yes --registry=https://registry.npmjs.org/ --prefer-offline" +
           L" --fetch-retries=4 --fetch-retry-factor=2" +
           L" --fetch-retry-mintimeout=1000 --fetch-retry-maxtimeout=15000" +
           L" --fetch-timeout=60000 --no-audit --no-fund --loglevel=http --timing " +
           std::wstring(kHarnessPackage) + L" " + kHarnessArgs;
}

LaunchSpec ResolveLaunchSpec() {
    std::wstring node = ResolvePath(L"node.exe");

    const std::wstring dshCmd = ResolvePath(L"dsh.cmd");
    if (!node.empty() && !dshCmd.empty()) {
        const fs::path prefix = fs::path(dshCmd).parent_path();
        const fs::path dshBin = prefix / L"node_modules" / L"@deepseek-ai" / L"dsh" / L"lib" / L"bin.js";
        if (IsRegularFile(dshBin)) {
            return {node, BuildDirectDshCommand(node, dshBin.wstring()), L"official global dsh"};
        }
    }

    const std::wstring npxCmd = ResolvePath(L"npx.cmd");
    if (!npxCmd.empty()) {
        const fs::path nodeRoot = fs::path(npxCmd).parent_path();
        if (node.empty()) {
            const fs::path siblingNode = nodeRoot / L"node.exe";
            if (IsRegularFile(siblingNode)) node = siblingNode.wstring();
        }
        const fs::path npxCli = nodeRoot / L"node_modules" / L"npm" / L"bin" / L"npx-cli.js";
        if (!node.empty() && IsRegularFile(npxCli)) {
            return {node, BuildDirectNpxCommand(node, npxCli.wstring()), L"official npx @deepseek-ai/dsh"};
        }
    }

    return {};
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
    if (Running() || ServiceReady()) return true;
    Stop();
    impl_->lastError.clear();

    const LaunchSpec launch = ResolveLaunchSpec();
    if (!launch.Valid()) {
        impl_->lastError = L"未找到可用的官方 Node.js/npm npx 运行入口。请重新运行 TuringDesk 一键部署以修复 Node.js。";
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
    const BOOL created = CreateProcessW(launch.application.c_str(), commandBuffer.data(), nullptr, nullptr, TRUE, flags,
                                        nullptr, home.empty() ? nullptr : home.c_str(), &startup, &processInfo);
    const DWORD createError = created ? ERROR_SUCCESS : GetLastError();
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
    return L"node.exe --dns-result-order=ipv4first npx-cli.js --yes --registry=https://registry.npmjs.org/ --prefer-offline --fetch-retries=4 --fetch-timeout=60000 --no-audit --no-fund --loglevel=http --timing @deepseek-ai/dsh web --no-open --host 127.0.0.1 --port 3080";
}

bool HarnessProcessManager::SelfTest() {
    const std::wstring sampleNode = L"C:\\Program Files\\nodejs\\node.exe";
    const std::wstring sampleNpx = L"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npx-cli.js";
    const std::wstring sampleDsh = L"C:\\Users\\test\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js";
    const std::wstring npxCommand = BuildDirectNpxCommand(sampleNode, sampleNpx);
    const std::wstring dshCommand = BuildDirectDshCommand(sampleNode, sampleDsh);

    const auto bindsOnlyLoopback = [](const std::wstring& command) {
        return command.find(L"--host 127.0.0.1") != std::wstring::npos &&
               command.find(L"--port 3080") != std::wstring::npos &&
               command.find(L"--host 0.0.0.0") == std::wstring::npos &&
               command.find(L"--host ::") == std::wstring::npos;
    };

    return DefaultUrl() == L"http://localhost:3080" &&
           npxCommand.find(L"npx-cli.js") != std::wstring::npos &&
           npxCommand.find(kHarnessPackage) != std::wstring::npos &&
           npxCommand.find(L"--dns-result-order=ipv4first") != std::wstring::npos &&
           npxCommand.find(L"--registry=https://registry.npmjs.org/") != std::wstring::npos &&
           npxCommand.find(L"--prefer-offline") != std::wstring::npos &&
           npxCommand.find(L"--loglevel=http") != std::wstring::npos &&
           npxCommand.find(L"--timing") != std::wstring::npos &&
           npxCommand.find(L"--no-open") != std::wstring::npos &&
           npxCommand.find(L"cmd.exe") == std::wstring::npos &&
           bindsOnlyLoopback(npxCommand) &&
           dshCommand.find(L"@deepseek-ai\\dsh\\lib\\bin.js") != std::wstring::npos &&
           dshCommand.find(L"--no-open") != std::wstring::npos &&
           bindsOnlyLoopback(dshCommand) &&
           std::string_view(kHarnessBootMarker) == "window.__DSH_BOOT__" &&
           !LogPath().empty();
}

} // namespace turingdesk
