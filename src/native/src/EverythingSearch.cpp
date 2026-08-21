#include "turingdesk/EverythingSearch.h"
#include <algorithm>
#include <atomic>
#include <chrono>
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

#pragma pack(push, 1)
struct EverythingIpcQuery2W {
    DWORD reply_hwnd;
    DWORD reply_copydata_message;
    DWORD search_flags;
    DWORD offset;
    DWORD max_results;
    DWORD request_flags;
    DWORD sort_type;
    wchar_t search_string[1];
};

struct EverythingIpcItem2 {
    DWORD flags;
    DWORD data_offset;
};

struct EverythingIpcList2 {
    DWORD totitems;
    DWORD numitems;
    DWORD offset;
    DWORD request_flags;
    DWORD sort_type;
    EverythingIpcItem2 items[1];
};
#pragma pack(pop)

constexpr DWORD kEverythingCopyDataQuery2W = 18;
constexpr DWORD kEverythingRequestFullPathAndName = 0x00000004;
constexpr DWORD kEverythingSortNameAscending = 1;
constexpr DWORD kEverythingFolder = 0x00000001;
std::atomic_bool gStartedBundledEverything{false};

struct FindContext { HWND hwnd{}; };

BOOL CALLBACK EnumEverythingWindows(HWND hwnd, LPARAM lParam) {
    wchar_t className[256]{};
    if (!GetClassNameW(hwnd, className, static_cast<int>(std::size(className)))) return TRUE;
    const std::wstring_view name(className);
    constexpr std::wstring_view prefix = L"EVERYTHING_TASKBAR_NOTIFICATION";
    if (!name.starts_with(prefix)) return TRUE;
    reinterpret_cast<FindContext*>(lParam)->hwnd = hwnd;
    return FALSE;
}

bool ReadFullPath(const std::byte* base, std::size_t size, DWORD offset, std::wstring& result) {
    if (offset > size || size - offset < sizeof(DWORD)) return false;
    DWORD chars = 0;
    std::memcpy(&chars, base + offset, sizeof(chars));
    const auto textOffset = static_cast<std::size_t>(offset) + sizeof(DWORD);
    const auto textBytes = static_cast<std::size_t>(chars) * sizeof(wchar_t);
    const auto bytesNeeded = textBytes + sizeof(wchar_t);
    if (textOffset > size || bytesNeeded > size - textOffset) return false;

    wchar_t terminator = L'X';
    std::memcpy(&terminator, base + textOffset + textBytes, sizeof(terminator));
    if (terminator != L'\0') return false;

    result.assign(chars, L'\0');
    if (textBytes > 0) std::memcpy(result.data(), base + textOffset, textBytes);
    return !result.empty();
}

fs::path BundledEverythingPath() {
    std::wstring modulePath(32768, L'\0');
    const DWORD count = GetModuleFileNameW(nullptr, modulePath.data(), static_cast<DWORD>(modulePath.size()));
    if (count == 0 || count >= modulePath.size()) return {};
    modulePath.resize(count);
    return fs::path(modulePath).parent_path() / L"Everything" / L"Everything.exe";
}

fs::path BundledConfigPath(const fs::path& executable) {
    return executable.parent_path() / L"TuringDesk-Everything.ini";
}

bool ConfigureBundledEverything(const fs::path& executable) {
    const auto config = BundledConfigPath(executable).wstring();
    return WritePrivateProfileStringW(L"Everything", L"run_in_background", L"1", config.c_str()) != FALSE &&
           WritePrivateProfileStringW(L"Everything", L"show_tray_icon", L"0", config.c_str()) != FALSE &&
           WritePrivateProfileStringW(L"Everything", L"check_for_updates_on_startup", L"0", config.c_str()) != FALSE;
}

bool LaunchBundledEverything(const std::wstring& action) {
    const auto executable = BundledEverythingPath();
    std::error_code ec;
    if (executable.empty() || !fs::exists(executable, ec)) return false;
    if (!ConfigureBundledEverything(executable)) return false;

    const auto config = BundledConfigPath(executable).wstring();
    std::wstring commandLine = L"\"" + executable.wstring() + L"\" -config \"" + config + L"\" " + action;
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), commandLine.data(), nullptr, nullptr, FALSE,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, nullptr, executable.parent_path().c_str(),
                        &startup, &process)) {
        return false;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
}

bool StartBundledEverything() {
    static std::atomic<ULONGLONG> lastAttempt{0};
    const ULONGLONG now = GetTickCount64();
    const ULONGLONG previous = lastAttempt.load(std::memory_order_relaxed);
    if (previous != 0 && now - previous < 3000) return false;
    lastAttempt.store(now, std::memory_order_relaxed);

    if (!LaunchBundledEverything(L"-startup")) return false;
    gStartedBundledEverything.store(true, std::memory_order_relaxed);
    return true;
}

} // namespace

HWND EverythingSearch::FindEverythingWindow() {
    if (HWND hwnd = FindWindowW(L"EVERYTHING_TASKBAR_NOTIFICATION", nullptr)) return hwnd;
    FindContext context;
    EnumWindows(EnumEverythingWindows, reinterpret_cast<LPARAM>(&context));
    return context.hwnd;
}

bool EverythingSearch::Available() const {
    if (FindEverythingWindow()) return true;
    if (!StartBundledEverything()) return false;

    for (int i = 0; i < 30; ++i) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        if (FindEverythingWindow()) return true;
    }
    return false;
}

bool EverythingSearch::Query(HWND replyWindow, const std::wstring& queryText, DWORD maxResults) const {
    if (!replyWindow || queryText.empty()) return false;
    HWND everything = FindEverythingWindow();
    if (!everything) {
        if (!Available()) return false;
        everything = FindEverythingWindow();
        if (!everything) return false;
    }

    const std::size_t bytes = sizeof(EverythingIpcQuery2W) - sizeof(wchar_t) +
                              (queryText.size() + 1) * sizeof(wchar_t);
    if (bytes > MAXDWORD) return false;

    std::vector<std::byte> storage(bytes);
    auto* query = reinterpret_cast<EverythingIpcQuery2W*>(storage.data());
    query->reply_hwnd = static_cast<DWORD>(reinterpret_cast<std::uintptr_t>(replyWindow));
    query->reply_copydata_message = kReplyId;
    query->search_flags = 0;
    query->offset = 0;
    query->max_results = maxResults;
    query->request_flags = kEverythingRequestFullPathAndName;
    query->sort_type = kEverythingSortNameAscending;
    std::copy(queryText.begin(), queryText.end(), query->search_string);
    query->search_string[queryText.size()] = L'\0';

    COPYDATASTRUCT copyData{};
    copyData.dwData = kEverythingCopyDataQuery2W;
    copyData.cbData = static_cast<DWORD>(bytes);
    copyData.lpData = query;

    DWORD_PTR result = 0;
    const auto sent = SendMessageTimeoutW(everything, WM_COPYDATA, reinterpret_cast<WPARAM>(replyWindow),
                                          reinterpret_cast<LPARAM>(&copyData),
                                          SMTO_ABORTIFHUNG | SMTO_BLOCK, 250, &result);
    return sent != 0 && result != 0;
}

bool EverythingSearch::HandleCopyData(const COPYDATASTRUCT* copyData, std::vector<SearchResult>& results) const {
    if (!copyData || copyData->dwData != kReplyId || !copyData->lpData) return false;

    constexpr std::size_t headerSize = offsetof(EverythingIpcList2, items);
    if (copyData->cbData < headerSize) return true;

    const auto* list = reinterpret_cast<const EverythingIpcList2*>(copyData->lpData);
    const auto* base = reinterpret_cast<const std::byte*>(copyData->lpData);
    const std::size_t size = copyData->cbData;
    if (list->numitems > (size - headerSize) / sizeof(EverythingIpcItem2)) return true;
    if ((list->request_flags & kEverythingRequestFullPathAndName) == 0) return true;

    results.clear();
    results.reserve(list->numitems);
    for (DWORD i = 0; i < list->numitems; ++i) {
        const auto& item = list->items[i];
        std::wstring fullPath;
        if (!ReadFullPath(base, size, item.data_offset, fullPath)) continue;

        std::wstring title = fullPath;
        const auto slash = title.find_last_of(L"\\/");
        if (slash != std::wstring::npos && slash + 1 < title.size()) title.erase(0, slash + 1);
        if (title.empty()) title = fullPath;

        const bool folder = (item.flags & kEverythingFolder) != 0;
        results.push_back({folder ? ResultKind::Folder : ResultKind::File,
                           std::move(title), fullPath, fullPath, 500.0 - static_cast<double>(i)});
    }
    return true;
}

bool EverythingSearch::SelfTest() const {
    const std::wstring expected = L"C:\\TuringDesk\\verify.txt";
    constexpr std::size_t headerSize = offsetof(EverythingIpcList2, items);
    const std::size_t itemEnd = headerSize + sizeof(EverythingIpcItem2);
    const std::size_t payloadBytes = sizeof(DWORD) + (expected.size() + 1) * sizeof(wchar_t);
    std::vector<std::byte> storage(itemEnd + payloadBytes);

    auto* list = reinterpret_cast<EverythingIpcList2*>(storage.data());
    list->totitems = 1;
    list->numitems = 1;
    list->offset = 0;
    list->request_flags = kEverythingRequestFullPathAndName;
    list->sort_type = kEverythingSortNameAscending;
    list->items[0].flags = 0;
    list->items[0].data_offset = static_cast<DWORD>(itemEnd);

    const DWORD chars = static_cast<DWORD>(expected.size());
    std::memcpy(storage.data() + itemEnd, &chars, sizeof(chars));
    std::memcpy(storage.data() + itemEnd + sizeof(chars), expected.c_str(), (expected.size() + 1) * sizeof(wchar_t));

    COPYDATASTRUCT copyData{};
    copyData.dwData = kReplyId;
    copyData.cbData = static_cast<DWORD>(storage.size());
    copyData.lpData = storage.data();

    std::vector<SearchResult> results;
    return HandleCopyData(&copyData, results) && results.size() == 1 &&
           results[0].kind == ResultKind::File && results[0].title == L"verify.txt" &&
           results[0].target == expected;
}

void EverythingSearch::Shutdown() const {
    if (!gStartedBundledEverything.exchange(false, std::memory_order_relaxed)) return;
    LaunchBundledEverything(L"-exit");
}

} // namespace turingdesk
