#include "turingdesk/EverythingSearch.h"
#include <algorithm>
#include <cstddef>
#include <vector>

namespace turingdesk {
namespace {

#pragma pack(push, 1)
struct EverythingIpcQueryW {
    HWND reply_hwnd;
    ULONG_PTR reply_copydata_message;
    DWORD search_flags;
    DWORD offset;
    DWORD max_results;
    wchar_t search_string[1];
};

struct EverythingIpcItemW {
    DWORD flags;
    DWORD filename_offset;
    DWORD path_offset;
};

struct EverythingIpcListW {
    DWORD totfolders;
    DWORD totfiles;
    DWORD totitems;
    DWORD numfolders;
    DWORD numfiles;
    DWORD numitems;
    DWORD offset;
    EverythingIpcItemW items[1];
};
#pragma pack(pop)

constexpr DWORD kEverythingCopyDataQueryW = 2;
constexpr DWORD kEverythingFolder = 0x00000001;
constexpr DWORD kEverythingDrive = 0x00000002;

struct FindContext { HWND hwnd{}; };

BOOL CALLBACK EnumEverythingWindows(HWND hwnd, LPARAM lParam) {
    wchar_t className[256]{};
    if (!GetClassNameW(hwnd, className, static_cast<int>(std::size(className)))) return TRUE;
    const std::wstring_view name(className);
    constexpr std::wstring_view prefix = L"EVERYTHING_TASKBAR_NOTIFICATION";
    if (!name.starts_with(prefix)) return TRUE;
    auto* context = reinterpret_cast<FindContext*>(lParam);
    context->hwnd = hwnd;
    return FALSE;
}

} // namespace

HWND EverythingSearch::FindEverythingWindow() {
    if (HWND hwnd = FindWindowW(L"EVERYTHING_TASKBAR_NOTIFICATION", nullptr)) return hwnd;
    FindContext context;
    EnumWindows(EnumEverythingWindows, reinterpret_cast<LPARAM>(&context));
    return context.hwnd;
}

bool EverythingSearch::Available() const {
    return FindEverythingWindow() != nullptr;
}

bool EverythingSearch::Query(HWND replyWindow, const std::wstring& queryText, DWORD maxResults) const {
    if (!replyWindow || queryText.empty()) return false;
    const HWND everything = FindEverythingWindow();
    if (!everything) return false;

    const std::size_t bytes = sizeof(EverythingIpcQueryW) - sizeof(wchar_t) +
                              (queryText.size() + 1) * sizeof(wchar_t);
    std::vector<std::byte> storage(bytes);
    auto* query = reinterpret_cast<EverythingIpcQueryW*>(storage.data());
    query->reply_hwnd = replyWindow;
    query->reply_copydata_message = kReplyId;
    query->search_flags = 0;
    query->offset = 0;
    query->max_results = maxResults;
    std::copy(queryText.begin(), queryText.end(), query->search_string);
    query->search_string[queryText.size()] = L'\0';

    COPYDATASTRUCT copyData{};
    copyData.dwData = kEverythingCopyDataQueryW;
    copyData.cbData = static_cast<DWORD>(bytes);
    copyData.lpData = query;

    DWORD_PTR result = 0;
    return SendMessageTimeoutW(everything, WM_COPYDATA, reinterpret_cast<WPARAM>(replyWindow),
                               reinterpret_cast<LPARAM>(&copyData),
                               SMTO_ABORTIFHUNG | SMTO_BLOCK, 250, &result) != 0 && result != 0;
}

bool EverythingSearch::HandleCopyData(const COPYDATASTRUCT* copyData, std::vector<SearchResult>& results) const {
    if (!copyData || copyData->dwData != kReplyId || !copyData->lpData ||
        copyData->cbData < sizeof(EverythingIpcListW) - sizeof(EverythingIpcItemW)) return false;

    const auto* list = reinterpret_cast<const EverythingIpcListW*>(copyData->lpData);
    const auto* base = reinterpret_cast<const std::byte*>(list);
    const std::size_t size = copyData->cbData;
    const std::size_t header = offsetof(EverythingIpcListW, items);
    if (header + static_cast<std::size_t>(list->numitems) * sizeof(EverythingIpcItemW) > size) return true;

    results.clear();
    results.reserve(list->numitems);
    for (DWORD i = 0; i < list->numitems; ++i) {
        const auto& item = list->items[i];
        if (item.filename_offset >= size || item.path_offset >= size) continue;
        const auto* filename = reinterpret_cast<const wchar_t*>(base + item.filename_offset);
        const auto* path = reinterpret_cast<const wchar_t*>(base + item.path_offset);

        const std::size_t filenameChars = (size - item.filename_offset) / sizeof(wchar_t);
        const std::size_t pathChars = (size - item.path_offset) / sizeof(wchar_t);
        if (std::find(filename, filename + filenameChars, L'\0') == filename + filenameChars) continue;
        if (std::find(path, path + pathChars, L'\0') == path + pathChars) continue;

        std::wstring fullPath;
        if ((item.flags & kEverythingDrive) != 0) {
            fullPath = filename;
        } else {
            fullPath = path;
            if (!fullPath.empty() && fullPath.back() != L'\\') fullPath.push_back(L'\\');
            fullPath += filename;
        }
        const bool folder = (item.flags & kEverythingFolder) != 0;
        results.push_back({folder ? ResultKind::Folder : ResultKind::File, filename, fullPath, fullPath, 500.0 - i});
    }
    return true;
}

} // namespace turingdesk
