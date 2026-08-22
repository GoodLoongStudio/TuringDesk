#include "turingdesk/GozSearch.h"
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <string_view>
#include <thread>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\goz-v1";
constexpr DWORD kQueryTimeoutMs = 5000;

#pragma pack(push, 1)
struct GozReplyHeader {
    std::uint32_t magic;
    std::uint32_t count;
};

struct GozReplyItem {
    std::uint32_t pathChars;
    std::uint32_t flags;
};
#pragma pack(pop)

constexpr std::uint32_t kReplyMagic = 0x315A4754; // TGZ1
constexpr std::uint32_t kDirectoryFlag = 0x1;

fs::path ModuleDirectory() {
    std::wstring path(32768, L'\0');
    const DWORD count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (count == 0 || count >= path.size()) return {};
    path.resize(count);
    return fs::path(path).parent_path();
}

std::wstring SearchExecutable(const wchar_t* name) {
    std::wstring buffer(32768, L'\0');
    const DWORD count = SearchPathW(nullptr, name, nullptr, static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (count == 0 || count >= buffer.size()) return {};
    buffer.resize(count);
    return buffer;
}

std::wstring QuoteArgument(const std::wstring& value) {
    if (value.empty()) return L"\"\"";
    if (value.find_first_of(L" \t\n\v\"") == std::wstring::npos) return value;

    std::wstring out = L"\"";
    std::size_t slashes = 0;
    for (wchar_t ch : value) {
        if (ch == L'\\') {
            ++slashes;
            continue;
        }
        if (ch == L'\"') {
            out.append(slashes * 2 + 1, L'\\');
            out.push_back(L'\"');
            slashes = 0;
            continue;
        }
        out.append(slashes, L'\\');
        slashes = 0;
        out.push_back(ch);
    }
    out.append(slashes * 2, L'\\');
    out.push_back(L'\"');
    return out;
}

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0)
        count = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring out(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count);
    return out;
}

std::vector<std::wstring> SplitPaths(const std::string& output, DWORD maxResults) {
    std::vector<std::wstring> paths;
    std::size_t start = 0;
    while (start < output.size() && paths.size() < maxResults) {
        const auto end = output.find('\n', start);
        std::string line = output.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty()) {
            auto path = Utf8ToWide(line);
            if (!path.empty()) paths.push_back(std::move(path));
        }
        if (end == std::string::npos) break;
        start = end + 1;
    }
    return paths;
}

bool RunGozQuery(const std::wstring& binary, const std::wstring& query, DWORD maxResults,
                 std::vector<std::wstring>& paths) {
    SECURITY_ATTRIBUTES security{};
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;

    HANDLE stdoutRead = nullptr;
    HANDLE stdoutWrite = nullptr;
    if (!CreatePipe(&stdoutRead, &stdoutWrite, &security, 0)) return false;
    SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);

    HANDLE nullInput = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    HANDLE nullError = CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = nullInput == INVALID_HANDLE_VALUE ? nullptr : nullInput;
    startup.hStdOutput = stdoutWrite;
    startup.hStdError = nullError == INVALID_HANDLE_VALUE ? stdoutWrite : nullError;

    PROCESS_INFORMATION process{};
    std::wstring command = QuoteArgument(binary) + L" -n " + std::to_wstring(maxResults) + L" " + QuoteArgument(query);
    const BOOL created = CreateProcessW(binary.c_str(), command.data(), nullptr, nullptr, TRUE,
                                        CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                                        nullptr, nullptr, &startup, &process);

    CloseHandle(stdoutWrite);
    if (nullInput && nullInput != INVALID_HANDLE_VALUE) CloseHandle(nullInput);
    if (nullError && nullError != INVALID_HANDLE_VALUE) CloseHandle(nullError);

    if (!created) {
        CloseHandle(stdoutRead);
        return false;
    }

    const DWORD wait = WaitForSingleObject(process.hProcess, kQueryTimeoutMs);
    if (wait == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, 1);
        WaitForSingleObject(process.hProcess, 1000);
    }

    std::string output;
    char buffer[8192];
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(stdoutRead, buffer, static_cast<DWORD>(sizeof(buffer)), &read, nullptr) || read == 0) break;
        output.append(buffer, read);
        if (output.size() > 1024 * 1024) break;
    }

    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    CloseHandle(stdoutRead);

    if (wait == WAIT_TIMEOUT || exitCode != 0) return false;
    paths = SplitPaths(output, maxResults);
    return true;
}

std::vector<std::byte> EncodeReply(const std::vector<std::wstring>& paths) {
    std::size_t bytes = sizeof(GozReplyHeader);
    for (const auto& path : paths)
        bytes += sizeof(GozReplyItem) + path.size() * sizeof(wchar_t);

    std::vector<std::byte> payload(bytes);
    auto* header = reinterpret_cast<GozReplyHeader*>(payload.data());
    header->magic = kReplyMagic;
    header->count = static_cast<std::uint32_t>(paths.size());

    std::size_t offset = sizeof(GozReplyHeader);
    for (const auto& path : paths) {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        GozReplyItem item{};
        item.pathChars = static_cast<std::uint32_t>(path.size());
        item.flags = attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY)
            ? kDirectoryFlag : 0;
        std::memcpy(payload.data() + offset, &item, sizeof(item));
        offset += sizeof(item);
        if (!path.empty()) {
            const auto pathBytes = path.size() * sizeof(wchar_t);
            std::memcpy(payload.data() + offset, path.data(), pathBytes);
            offset += pathBytes;
        }
    }
    return payload;
}

} // namespace

GozSearch::GozSearch() : state_(std::make_shared<SharedState>()) {}

std::wstring GozSearch::FindClientBinary() {
    wchar_t explicitPath[32768]{};
    const DWORD explicitCount = GetEnvironmentVariableW(L"TURINGDESK_GOZ_CLI", explicitPath,
                                                         static_cast<DWORD>(std::size(explicitPath)));
    if (explicitCount > 0 && explicitCount < std::size(explicitPath)) {
        std::error_code ec;
        if (fs::exists(explicitPath, ec)) return explicitPath;
    }

    const auto bundled = ModuleDirectory() / L"Goz" / L"goz.exe";
    std::error_code ec;
    if (!bundled.empty() && fs::exists(bundled, ec)) return bundled.wstring();

    return SearchExecutable(L"goz.exe");
}

bool GozSearch::PipeAvailable() {
    if (WaitNamedPipeW(kPipeName, 0)) return true;
    const DWORD error = GetLastError();
    return error == ERROR_SEM_TIMEOUT || error == ERROR_PIPE_BUSY;
}

bool GozSearch::Available() const {
    return !FindClientBinary().empty() && PipeAvailable();
}

bool GozSearch::Query(HWND replyWindow, const std::wstring& query, DWORD maxResults) const {
    if (!replyWindow || !IsWindow(replyWindow) || query.empty() || maxResults == 0) return false;
    const auto binary = FindClientBinary();
    if (binary.empty() || !PipeAvailable()) return false;

    const auto state = state_;
    const std::uint64_t generation = state->generation.fetch_add(1, std::memory_order_relaxed) + 1;
    std::thread([state, generation, binary, replyWindow, query, maxResults]() {
        std::vector<std::wstring> paths;
        if (!RunGozQuery(binary, query, maxResults, paths)) return;
        if (state->generation.load(std::memory_order_relaxed) != generation || !IsWindow(replyWindow)) return;

        auto payload = EncodeReply(paths);
        COPYDATASTRUCT copyData{};
        copyData.dwData = GozSearch::kReplyId;
        copyData.cbData = static_cast<DWORD>(payload.size());
        copyData.lpData = payload.data();
        DWORD_PTR ignored = 0;
        SendMessageTimeoutW(replyWindow, WM_COPYDATA, 0, reinterpret_cast<LPARAM>(&copyData),
                            SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, &ignored);
    }).detach();
    return true;
}

bool GozSearch::HandleCopyData(const COPYDATASTRUCT* copyData, std::vector<SearchResult>& results) const {
    if (!copyData || copyData->dwData != kReplyId || !copyData->lpData) return false;
    if (copyData->cbData < sizeof(GozReplyHeader)) return true;

    const auto* base = reinterpret_cast<const std::byte*>(copyData->lpData);
    GozReplyHeader header{};
    std::memcpy(&header, base, sizeof(header));
    if (header.magic != kReplyMagic) return true;

    std::size_t offset = sizeof(GozReplyHeader);
    results.clear();
    results.reserve(header.count);
    for (std::uint32_t i = 0; i < header.count; ++i) {
        if (offset > copyData->cbData || copyData->cbData - offset < sizeof(GozReplyItem)) break;
        GozReplyItem item{};
        std::memcpy(&item, base + offset, sizeof(item));
        offset += sizeof(item);

        const std::size_t pathBytes = static_cast<std::size_t>(item.pathChars) * sizeof(wchar_t);
        if (offset > copyData->cbData || pathBytes > copyData->cbData - offset) break;
        std::wstring fullPath(item.pathChars, L'\0');
        if (pathBytes) std::memcpy(fullPath.data(), base + offset, pathBytes);
        offset += pathBytes;
        if (fullPath.empty()) continue;

        fs::path path(fullPath);
        std::wstring title = path.filename().wstring();
        if (title.empty()) title = fullPath;
        const bool directory = (item.flags & kDirectoryFlag) != 0;
        results.push_back({directory ? ResultKind::Folder : ResultKind::File,
                           std::move(title), fullPath, fullPath,
                           500.0 - static_cast<double>(i)});
    }
    return true;
}

bool GozSearch::SelfTest() const {
    const std::vector<std::wstring> expected{L"C:\\TuringDesk\\verify.txt", L"C:\\TuringDesk\\Folder"};
    auto payload = EncodeReply(expected);
    auto* second = reinterpret_cast<GozReplyItem*>(payload.data() + sizeof(GozReplyHeader) +
        sizeof(GozReplyItem) + expected[0].size() * sizeof(wchar_t));
    second->flags |= kDirectoryFlag;

    COPYDATASTRUCT copyData{};
    copyData.dwData = kReplyId;
    copyData.cbData = static_cast<DWORD>(payload.size());
    copyData.lpData = payload.data();
    std::vector<SearchResult> results;
    return HandleCopyData(&copyData, results) && results.size() == 2 &&
           results[0].kind == ResultKind::File && results[0].title == L"verify.txt" &&
           results[1].kind == ResultKind::Folder && results[1].title == L"Folder";
}

void GozSearch::Shutdown() const {
    if (state_) state_->generation.fetch_add(1, std::memory_order_relaxed);
}

} // namespace turingdesk
