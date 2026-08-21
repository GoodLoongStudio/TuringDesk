#include "turingdesk/AppSearch.h"
#include <windows.h>
#include <filesystem>
#include <algorithm>
#include <cwctype>
#include <iterator>
#include <unordered_set>
#include <utility>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

double ScoreText(const std::wstring& haystackRaw, const std::wstring& needleRaw) {
    const auto haystack = Lower(haystackRaw);
    const auto needle = Lower(needleRaw);
    if (needle.empty()) return 0.0;
    if (haystack == needle) return 1000.0;
    if (haystack.starts_with(needle)) return 850.0 - static_cast<double>(haystack.size() - needle.size());
    if (const auto pos = haystack.find(needle); pos != std::wstring::npos)
        return 650.0 - static_cast<double>(pos) * 2.0;

    std::size_t h = 0;
    std::size_t gaps = 0;
    for (wchar_t n : needle) {
        const auto found = haystack.find(n, h);
        if (found == std::wstring::npos) return 0.0;
        gaps += found - h;
        h = found + 1;
    }
    return 350.0 - static_cast<double>(gaps);
}

void AddPathEntries(const fs::path& root, std::vector<AppSearch::Entry>& entries) {
    std::error_code ec;
    if (!fs::exists(root, ec)) return;
    fs::recursive_directory_iterator it(root, fs::directory_options::skip_permission_denied, ec), end;
    for (; it != end; it.increment(ec)) {
        if (ec) { ec.clear(); continue; }
        if (!it->is_regular_file(ec)) continue;
        auto ext = Lower(it->path().extension().wstring());
        if (ext != L".lnk" && ext != L".url" && ext != L".exe") continue;
        auto name = it->path().stem().wstring();
        if (name.empty()) continue;
        entries.push_back({name, it->path().wstring(), it->path().parent_path().wstring()});
    }
}

void AddRegistryAppPaths(HKEY root, REGSAM view, std::vector<AppSearch::Entry>& entries) {
    HKEY key{};
    constexpr wchar_t kPath[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths";
    if (RegOpenKeyExW(root, kPath, 0, KEY_READ | view, &key) != ERROR_SUCCESS) return;

    DWORD index = 0;
    wchar_t subkeyName[512];
    while (true) {
        DWORD nameLen = static_cast<DWORD>(std::size(subkeyName));
        const auto status = RegEnumKeyExW(key, index++, subkeyName, &nameLen, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) continue;

        HKEY appKey{};
        if (RegOpenKeyExW(key, subkeyName, 0, KEY_READ | view, &appKey) != ERROR_SUCCESS) continue;
        wchar_t value[32768];
        DWORD type = 0;
        DWORD bytes = sizeof(value);
        if (RegQueryValueExW(appKey, nullptr, nullptr, &type, reinterpret_cast<BYTE*>(value), &bytes) == ERROR_SUCCESS &&
            (type == REG_SZ || type == REG_EXPAND_SZ)) {
            std::wstring target(value, bytes / sizeof(wchar_t));
            while (!target.empty() && target.back() == L'\0') target.pop_back();
            if (type == REG_EXPAND_SZ) {
                wchar_t expanded[32768];
                const DWORD len = ExpandEnvironmentStringsW(target.c_str(), expanded, static_cast<DWORD>(std::size(expanded)));
                if (len > 0 && len < std::size(expanded)) target.assign(expanded);
            }
            std::wstring name(subkeyName, nameLen);
            if (Lower(name).ends_with(L".exe")) name.resize(name.size() - 4);
            entries.push_back({name, target, L"App Paths"});
        }
        RegCloseKey(appKey);
    }
    RegCloseKey(key);
}

} // namespace

void AppSearch::BuildIndex() {
    entries_.clear();

    wchar_t buffer[MAX_PATH];
    if (GetEnvironmentVariableW(L"APPDATA", buffer, MAX_PATH))
        AddPathEntries(fs::path(buffer) / L"Microsoft/Windows/Start Menu/Programs", entries_);
    if (GetEnvironmentVariableW(L"ProgramData", buffer, MAX_PATH))
        AddPathEntries(fs::path(buffer) / L"Microsoft/Windows/Start Menu/Programs", entries_);

    AddRegistryAppPaths(HKEY_CURRENT_USER, 0, entries_);
    AddRegistryAppPaths(HKEY_LOCAL_MACHINE, KEY_WOW64_64KEY, entries_);
    AddRegistryAppPaths(HKEY_LOCAL_MACHINE, KEY_WOW64_32KEY, entries_);

    const std::pair<const wchar_t*, const wchar_t*> builtins[] = {
        {L"Explorer", L"explorer.exe"}, {L"Notepad", L"notepad.exe"},
        {L"Command Prompt", L"cmd.exe"}, {L"PowerShell", L"powershell.exe"},
        {L"Task Manager", L"taskmgr.exe"}, {L"Control Panel", L"control.exe"},
        {L"Windows Terminal", L"wt.exe"}
    };
    for (const auto& [name, target] : builtins) entries_.push_back({name, target, L"Windows"});

    std::unordered_set<std::wstring> seen;
    std::vector<Entry> deduped;
    deduped.reserve(entries_.size());
    for (auto& entry : entries_) {
        const auto key = Lower(entry.name + L"|" + entry.target);
        if (seen.insert(key).second) deduped.push_back(std::move(entry));
    }
    entries_ = std::move(deduped);
}

std::vector<SearchResult> AppSearch::Query(const std::wstring& query, std::size_t maxResults) const {
    std::vector<SearchResult> results;
    if (query.empty()) return results;
    for (const auto& entry : entries_) {
        double score = std::max(ScoreText(entry.name, query), ScoreText(entry.keywords, query) * 0.85);
        if (score <= 0.0) continue;
        results.push_back({ResultKind::App, entry.name, entry.target, entry.target, score});
    }
    std::sort(results.begin(), results.end(), [](const SearchResult& a, const SearchResult& b) {
        if (a.score != b.score) return a.score > b.score;
        return a.title < b.title;
    });
    if (results.size() > maxResults) results.resize(maxResults);
    return results;
}

} // namespace turingdesk
