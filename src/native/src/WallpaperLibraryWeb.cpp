#include "turingdesk/WallpaperLibrary.h"

#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <chrono>
#include <cwctype>

namespace turingdesk::wallpaper {
namespace {

std::wstring Trim(std::wstring value) {
    while (!value.empty() && std::iswspace(value.front())) value.erase(value.begin());
    while (!value.empty() && std::iswspace(value.back())) value.pop_back();
    return value;
}

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

bool StartsWithInsensitive(std::wstring_view value, std::wstring_view prefix) {
    if (value.size() < prefix.size()) return false;
    for (std::size_t i = 0; i < prefix.size(); ++i) {
        if (std::towlower(value[i]) != std::towlower(prefix[i])) return false;
    }
    return true;
}

std::wstring NewWebId() {
    GUID guid{};
    if (SUCCEEDED(CoCreateGuid(&guid))) {
        wchar_t text[64]{};
        if (StringFromGUID2(guid, text, static_cast<int>(std::size(text))) > 0) {
            std::wstring id = L"web-";
            for (wchar_t ch : std::wstring_view(text)) {
                if (ch != L'{' && ch != L'}' && ch != L'-') id.push_back(static_cast<wchar_t>(std::towlower(ch)));
            }
            return id;
        }
    }
    return L"web-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
}

unsigned long long NowUnixSeconds() {
    return static_cast<unsigned long long>(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
}

std::wstring DefaultWebTitle(std::wstring_view url) {
    std::wstring value(url);
    const std::size_t start = value.find(L"://");
    if (start == std::wstring::npos) return L"Web 壁纸";
    const std::size_t hostStart = start + 3;
    const std::size_t hostEnd = value.find_first_of(L"/?#", hostStart);
    const std::wstring host = hostEnd == std::wstring::npos ? value.substr(hostStart) : value.substr(hostStart, hostEnd - hostStart);
    return host.empty() ? L"Web 壁纸" : host;
}

bool SameUrl(std::wstring_view a, std::wstring_view b) {
    std::wstring left = Trim(std::wstring(a));
    std::wstring right = Trim(std::wstring(b));
    if (!StartsWithInsensitive(left, L"https://") || !StartsWithInsensitive(right, L"https://"))
        return left == right;

    constexpr std::size_t authorityStart = 8;
    const std::size_t leftEnd = left.find_first_of(L"/?#", authorityStart);
    const std::size_t rightEnd = right.find_first_of(L"/?#", authorityStart);
    const std::size_t leftAuthorityEnd = leftEnd == std::wstring::npos ? left.size() : leftEnd;
    const std::size_t rightAuthorityEnd = rightEnd == std::wstring::npos ? right.size() : rightEnd;

    const std::wstring leftAuthority = Lower(left.substr(authorityStart, leftAuthorityEnd - authorityStart));
    const std::wstring rightAuthority = Lower(right.substr(authorityStart, rightAuthorityEnd - authorityStart));
    if (leftAuthority != rightAuthority) return false;

    const std::wstring_view leftTail(left.data() + leftAuthorityEnd, left.size() - leftAuthorityEnd);
    const std::wstring_view rightTail(right.data() + rightAuthorityEnd, right.size() - rightAuthorityEnd);
    return leftTail == rightTail;
}

} // namespace

bool WallpaperLibrary::IsTrustedWebUrl(std::wstring_view input) noexcept {
    const std::wstring value = Trim(std::wstring(input));
    constexpr std::wstring_view prefix = L"https://";
    if (value.size() <= prefix.size() || !StartsWithInsensitive(value, prefix)) return false;

    const std::size_t hostStart = prefix.size();
    const std::size_t hostEnd = value.find_first_of(L"/?#", hostStart);
    const std::size_t authorityEnd = hostEnd == std::wstring::npos ? value.size() : hostEnd;
    if (authorityEnd <= hostStart) return false;

    const std::wstring_view authority(value.data() + hostStart, authorityEnd - hostStart);
    if (authority.find(L'@') != std::wstring_view::npos) return false;
    for (const wchar_t ch : authority) {
        if (std::iswspace(ch) || std::iswcntrl(ch)) return false;
    }
    return true;
}

std::optional<WallpaperLibraryItem> WallpaperLibrary::ImportWebUrl(
    std::wstring url, std::wstring title, std::wstring* error) {
    if (error) error->clear();
    url = Trim(std::move(url));
    if (!IsTrustedWebUrl(url)) {
        if (error) *error = L"远程 Web 壁纸只允许不含凭据的 HTTPS URL";
        return std::nullopt;
    }

    for (auto& item : items_) {
        if (item.kind != LibraryWallpaperKind::Web || item.source.empty()) continue;
        if (!SameUrl(item.source.wstring(), url)) continue;
        if (!title.empty()) item.title = Trim(std::move(title));
        item.lastUsedUnixSeconds = NowUnixSeconds();
        if (!SaveItem(item, error)) return std::nullopt;
        return item;
    }

    WallpaperLibraryItem item;
    item.id = NewWebId();
    item.kind = LibraryWallpaperKind::Web;
    item.title = Trim(std::move(title));
    if (item.title.empty()) item.title = DefaultWebTitle(url);
    item.source = std::filesystem::path(url);
    item.favorite = false;
    item.managedCopy = false;
    item.importedUnixSeconds = NowUnixSeconds();
    item.lastUsedUnixSeconds = item.importedUnixSeconds;
    if (!SaveItem(item, error)) return std::nullopt;
    items_.insert(items_.begin(), item);
    return item;
}

} // namespace turingdesk::wallpaper
