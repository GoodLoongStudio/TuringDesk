#include "turingdesk/WallpaperApplicationRules.h"

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <cwctype>
#include <system_error>
#include <utility>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr ULONGLONG kCacheRefreshMs = 1000;
WallpaperApplicationRules g_cachedRules;
ULONGLONG g_cacheTick{};
bool g_cacheLoaded{};

fs::path DefaultStoragePath() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path root = (length > 0 && length < std::size(local))
        ? fs::path(local)
        : fs::temp_directory_path();
    return root / L"TuringDesk" / L"WallpaperLibrary" / L"application-rules.ini";
}

void SetError(std::wstring* error, std::wstring value) {
    if (error) *error = std::move(value);
}

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

bool SameText(std::wstring_view a, std::wstring_view b) noexcept {
    if (a.size() != b.size()) return false;
    const std::wstring left(a);
    const std::wstring right(b);
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

std::wstring SectionName(std::size_t index) {
    wchar_t section[48]{};
    swprintf_s(section, L"Rule.%04zu", index);
    return section;
}

std::wstring ReadText(const fs::path& path, const wchar_t* section, const wchar_t* key) {
    std::vector<wchar_t> buffer(32768);
    GetPrivateProfileStringW(section, key, L"", buffer.data(), static_cast<DWORD>(buffer.size()), path.c_str());
    return buffer.data();
}

std::wstring WindowTitle(HWND hwnd) {
    const int length = GetWindowTextLengthW(hwnd);
    if (length <= 0) return {};
    std::wstring result(static_cast<std::size_t>(length) + 1, L'\0');
    GetWindowTextW(hwnd, result.data(), length + 1);
    result.resize(static_cast<std::size_t>(length));
    return result;
}

std::wstring WindowExecutable(HWND hwnd) {
    DWORD processId = 0;
    GetWindowThreadProcessId(hwnd, &processId);
    if (processId == 0 || processId == GetCurrentProcessId()) return {};

    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (!process) return {};
    std::vector<wchar_t> buffer(32768);
    DWORD size = static_cast<DWORD>(buffer.size());
    std::wstring result;
    if (QueryFullProcessImageNameW(process, 0, buffer.data(), &size) && size > 0) {
        result.assign(buffer.data(), size);
    }
    CloseHandle(process);
    return WallpaperApplicationRules::NormalizeExecutable(std::move(result));
}

bool IsFullscreenWindow(HWND hwnd) {
    if (!hwnd || IsIconic(hwnd)) return false;
    RECT windowRect{};
    if (!GetWindowRect(hwnd, &windowRect)) return false;
    const HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return false;
    constexpr LONG tolerance = 2;
    return std::abs(windowRect.left - info.rcMonitor.left) <= tolerance &&
           std::abs(windowRect.top - info.rcMonitor.top) <= tolerance &&
           std::abs(windowRect.right - info.rcMonitor.right) <= tolerance &&
           std::abs(windowRect.bottom - info.rcMonitor.bottom) <= tolerance;
}

bool TriggerMatches(ApplicationRuleTrigger trigger, HWND hwnd, HWND foreground) {
    switch (trigger) {
    case ApplicationRuleTrigger::Foreground:
        return foreground && (hwnd == foreground || GetAncestor(hwnd, GA_ROOT) == GetAncestor(foreground, GA_ROOT));
    case ApplicationRuleTrigger::Running:
        return true;
    case ApplicationRuleTrigger::Fullscreen:
        return IsFullscreenWindow(hwnd);
    case ApplicationRuleTrigger::Maximized:
        return IsZoomed(hwnd) != FALSE;
    }
    return false;
}

struct WindowCandidate {
    HWND hwnd{};
    std::wstring executable;
    std::wstring title;
};

std::vector<WindowCandidate> EnumerateCandidateWindows(HWND wallpaperWindow, HWND settingsWindow) {
    struct Context {
        HWND wallpaper{};
        HWND settings{};
        std::vector<WindowCandidate> windows;
    } context{wallpaperWindow, settingsWindow, {}};

    EnumWindows([](HWND hwnd, LPARAM raw) -> BOOL {
        auto* context = reinterpret_cast<Context*>(raw);
        if (!context || !hwnd) return TRUE;
        if (hwnd == context->wallpaper || hwnd == context->settings) return TRUE;
        if (!IsWindowVisible(hwnd)) return TRUE;
        if (GetWindow(hwnd, GW_OWNER) != nullptr && (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_APPWINDOW) == 0) return TRUE;
        const LONG_PTR exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return TRUE;

        std::wstring executable = WindowExecutable(hwnd);
        if (executable.empty()) return TRUE;
        context->windows.push_back(WindowCandidate{hwnd, std::move(executable), WindowTitle(hwnd)});
        return TRUE;
    }, reinterpret_cast<LPARAM>(&context));
    return context.windows;
}

bool BetterMatch(const ApplicationRuleMatch& candidate, const ApplicationRuleMatch& current) {
    if (!current.matched) return true;
    if (candidate.priority != current.priority) return candidate.priority > current.priority;
    return static_cast<int>(candidate.action) > static_cast<int>(current.action);
}

} // namespace

WallpaperApplicationRules::WallpaperApplicationRules() : storagePath_(DefaultStoragePath()) {}
WallpaperApplicationRules::WallpaperApplicationRules(fs::path storagePath) : storagePath_(std::move(storagePath)) {}

bool WallpaperApplicationRules::Load(std::wstring* error) {
    SetError(error, L"");
    items_.clear();
    std::error_code ec;
    if (!fs::exists(storagePath_, ec)) return true;

    const int rawCount = static_cast<int>(GetPrivateProfileIntW(L"Rules", L"Count", 0, storagePath_.c_str()));
    const int count = std::clamp(rawCount, 0, 2048);
    for (int i = 0; i < count; ++i) {
        const std::wstring section = SectionName(static_cast<std::size_t>(i));
        WallpaperApplicationRule rule;
        rule.id = ReadText(storagePath_, section.c_str(), L"Id");
        rule.executable = NormalizeExecutable(ReadText(storagePath_, section.c_str(), L"Executable"));
        rule.displayName = ReadText(storagePath_, section.c_str(), L"DisplayName");
        rule.enabled = GetPrivateProfileIntW(section.c_str(), L"Enabled", 1, storagePath_.c_str()) != 0;
        rule.trigger = ParseTrigger(ReadText(storagePath_, section.c_str(), L"Trigger"));
        rule.action = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"Action"));
        rule.priority = std::clamp(static_cast<int>(GetPrivateProfileIntW(section.c_str(), L"Priority", 100, storagePath_.c_str())), 0, 10000);
        if (rule.id.empty()) rule.id = MakeId();
        if (rule.executable.empty()) continue;
        items_.push_back(std::move(rule));
    }
    return true;
}

bool WallpaperApplicationRules::Save(std::wstring* error) const {
    SetError(error, L"");
    std::error_code ec;
    fs::create_directories(storagePath_.parent_path(), ec);
    if (ec) {
        SetError(error, L"无法创建应用规则目录，error=" + std::to_wstring(ec.value()));
        return false;
    }

    fs::path temporary = storagePath_;
    temporary += L".tmp";
    DeleteFileW(temporary.c_str());
    bool ok = WritePrivateProfileStringW(L"Rules", L"Version", L"1", temporary.c_str()) != FALSE;
    const std::wstring count = std::to_wstring(items_.size());
    ok = (WritePrivateProfileStringW(L"Rules", L"Count", count.c_str(), temporary.c_str()) != FALSE) && ok;
    for (std::size_t i = 0; i < items_.size(); ++i) {
        const auto& rule = items_[i];
        const std::wstring section = SectionName(i);
        const std::wstring priority = std::to_wstring(std::clamp(rule.priority, 0, 10000));
        ok = (WritePrivateProfileStringW(section.c_str(), L"Id", rule.id.c_str(), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"Executable", rule.executable.c_str(), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"DisplayName", rule.displayName.c_str(), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"Enabled", rule.enabled ? L"1" : L"0", temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"Trigger", TriggerKey(rule.trigger), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"Action", PerformanceActionKey(rule.action), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"Priority", priority.c_str(), temporary.c_str()) != FALSE) && ok;
    }
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, temporary.c_str());
    if (!ok) {
        DeleteFileW(temporary.c_str());
        SetError(error, L"写入应用规则失败");
        return false;
    }
    if (!MoveFileExW(temporary.c_str(), storagePath_.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const DWORD win32 = GetLastError();
        DeleteFileW(temporary.c_str());
        SetError(error, L"提交应用规则失败，Win32=" + std::to_wstring(win32));
        return false;
    }
    InvalidateApplicationRuleCache();
    return true;
}

bool WallpaperApplicationRules::Upsert(WallpaperApplicationRule rule, std::wstring* error) {
    SetError(error, L"");
    rule.executable = NormalizeExecutable(std::move(rule.executable));
    rule.priority = std::clamp(rule.priority, 0, 10000);
    if (rule.executable.empty()) {
        SetError(error, L"EXE 名称不能为空");
        return false;
    }
    if (rule.id.empty()) rule.id = MakeId();
    if (rule.displayName.empty()) rule.displayName = rule.executable;
    if (const auto index = FindIndex(rule.id)) items_[*index] = std::move(rule);
    else items_.push_back(std::move(rule));
    return Save(error);
}

bool WallpaperApplicationRules::Remove(std::wstring_view id, std::wstring* error) {
    SetError(error, L"");
    const auto index = FindIndex(id);
    if (!index) return true;
    items_.erase(items_.begin() + static_cast<std::ptrdiff_t>(*index));
    return Save(error);
}

void WallpaperApplicationRules::Clear() {
    items_.clear();
}

const std::vector<WallpaperApplicationRule>& WallpaperApplicationRules::Items() const noexcept { return items_; }

std::optional<WallpaperApplicationRule> WallpaperApplicationRules::Find(std::wstring_view id) const {
    if (const auto index = FindIndex(id)) return items_[*index];
    return std::nullopt;
}

const fs::path& WallpaperApplicationRules::StoragePath() const noexcept { return storagePath_; }

ApplicationRuleMatch WallpaperApplicationRules::Evaluate(HWND wallpaperWindow, HWND settingsWindow) const {
    ApplicationRuleMatch best;
    if (items_.empty()) return best;
    const HWND foreground = GetForegroundWindow();
    const auto windows = EnumerateCandidateWindows(wallpaperWindow, settingsWindow);

    for (const auto& rule : items_) {
        if (!rule.enabled || rule.executable.empty()) continue;
        for (const auto& window : windows) {
            if (!SameText(rule.executable, window.executable)) continue;
            if (!TriggerMatches(rule.trigger, window.hwnd, foreground)) continue;
            ApplicationRuleMatch candidate;
            candidate.matched = true;
            candidate.ruleId = rule.id;
            candidate.executable = rule.executable;
            candidate.displayName = rule.displayName;
            candidate.windowTitle = window.title;
            candidate.trigger = rule.trigger;
            candidate.action = rule.action;
            candidate.priority = rule.priority;
            candidate.window = window.hwnd;
            if (BetterMatch(candidate, best)) best = std::move(candidate);
        }
    }
    return best;
}

std::wstring WallpaperApplicationRules::NormalizeExecutable(std::wstring value) {
    if (value.empty()) return {};
    std::replace(value.begin(), value.end(), L'/', L'\\');
    const std::size_t slash = value.find_last_of(L'\\');
    if (slash != std::wstring::npos) value.erase(0, slash + 1);
    while (!value.empty() && std::iswspace(value.front())) value.erase(value.begin());
    while (!value.empty() && std::iswspace(value.back())) value.pop_back();
    return Lower(std::move(value));
}

std::wstring WallpaperApplicationRules::MakeId(std::wstring_view prefix) {
    const auto ticks = GetTickCount64();
    const auto now = std::chrono::high_resolution_clock::now().time_since_epoch().count();
    return std::wstring(prefix) + L"-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
           std::to_wstring(ticks) + L"-" + std::to_wstring(static_cast<unsigned long long>(now));
}

const wchar_t* WallpaperApplicationRules::TriggerKey(ApplicationRuleTrigger trigger) noexcept {
    switch (trigger) {
    case ApplicationRuleTrigger::Running: return L"running";
    case ApplicationRuleTrigger::Fullscreen: return L"fullscreen";
    case ApplicationRuleTrigger::Maximized: return L"maximized";
    case ApplicationRuleTrigger::Foreground: break;
    }
    return L"foreground";
}

const wchar_t* WallpaperApplicationRules::TriggerDisplayName(ApplicationRuleTrigger trigger) noexcept {
    switch (trigger) {
    case ApplicationRuleTrigger::Running: return L"应用运行时";
    case ApplicationRuleTrigger::Fullscreen: return L"该应用全屏时";
    case ApplicationRuleTrigger::Maximized: return L"该应用最大化时";
    case ApplicationRuleTrigger::Foreground: break;
    }
    return L"该应用在前台时";
}

ApplicationRuleTrigger WallpaperApplicationRules::ParseTrigger(std::wstring_view value) noexcept {
    const std::wstring key = Lower(std::wstring(value));
    if (key == L"running") return ApplicationRuleTrigger::Running;
    if (key == L"fullscreen") return ApplicationRuleTrigger::Fullscreen;
    if (key == L"maximized") return ApplicationRuleTrigger::Maximized;
    return ApplicationRuleTrigger::Foreground;
}

std::optional<std::size_t> WallpaperApplicationRules::FindIndex(std::wstring_view id) const noexcept {
    for (std::size_t i = 0; i < items_.size(); ++i) if (SameText(items_[i].id, id)) return i;
    return std::nullopt;
}

bool WallpaperApplicationRules::SelfTest() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-AppRules-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    WallpaperApplicationRules rules(root / L"rules.ini");
    std::wstring error;
    bool ok = rules.Load(&error);
    WallpaperApplicationRule first;
    first.id = L"game";
    first.executable = L"C:/Games/TestGame.EXE";
    first.displayName = L"Test Game";
    first.trigger = ApplicationRuleTrigger::Fullscreen;
    first.action = PerformanceAction::Pause;
    first.priority = 250;
    ok = ok && rules.Upsert(first, &error);

    WallpaperApplicationRule second;
    second.id = L"editor";
    second.executable = L"editor.exe";
    second.trigger = ApplicationRuleTrigger::Foreground;
    second.action = PerformanceAction::Normal;
    ok = ok && rules.Upsert(second, &error);
    ok = ok && rules.Items().size() == 2;

    WallpaperApplicationRules loaded(root / L"rules.ini");
    ok = ok && loaded.Load(&error);
    const auto game = loaded.Find(L"GAME");
    ok = ok && game.has_value() && game->executable == L"testgame.exe" &&
         game->trigger == ApplicationRuleTrigger::Fullscreen && game->priority == 250;
    ok = ok && NormalizeExecutable(L"D:\\Apps\\FOO.Exe") == L"foo.exe";
    ok = ok && ParseTrigger(L"MAXIMIZED") == ApplicationRuleTrigger::Maximized;
    ok = ok && loaded.Remove(L"editor", &error) && loaded.Items().size() == 1;

    fs::remove_all(root, ec);
    return ok;
}

ApplicationRuleMatch EvaluateCachedApplicationRules(HWND wallpaperWindow, HWND settingsWindow) {
    const ULONGLONG now = GetTickCount64();
    if (!g_cacheLoaded || now - g_cacheTick >= kCacheRefreshMs) {
        std::wstring ignored;
        g_cachedRules.Load(&ignored);
        g_cacheLoaded = true;
        g_cacheTick = now;
    }
    return g_cachedRules.Evaluate(wallpaperWindow, settingsWindow);
}

void InvalidateApplicationRuleCache() noexcept {
    g_cacheLoaded = false;
    g_cacheTick = 0;
}

} // namespace turingdesk::wallpaper
