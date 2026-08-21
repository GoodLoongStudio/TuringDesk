#include "turingdesk/EverythingSearch.h"
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <string_view>
#include <vector>

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
    const auto bytesNeeded = (static_cast<std::size_t>(chars) + 1) * sizeof(wchar_t);
    if (textOffset > size || bytesNeeded > size - textOffset) return false;
    const auto* text = reinterpret_cast<const wchar_t*>(base + textOffset);
    if (text[chars] != L'\0') return false;
    result.assign(text, chars);
    return !result.empty();
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
    const auto itemsBytes = static_cast<std::size_t>(list->numitems) * sizeof(EverythingIpcItem2);
    if (list->numitems > (size - headerSize) / sizeof(EverythingIpcItem2) || headerSize + itemsBytes > size) return true;

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

} // namespace turingdesk
