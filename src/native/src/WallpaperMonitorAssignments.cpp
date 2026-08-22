#include "turingdesk/WallpaperMonitorAssignments.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <system_error>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

fs::path DefaultStoragePath() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path root = (length > 0 && length < std::size(local))
        ? fs::path(local)
        : fs::temp_directory_path();
    return root / L"TuringDesk" / L"WallpaperLibrary" / L"monitor-assignments.ini";
}

unsigned long long NowUnixSeconds() {
    return static_cast<unsigned long long>(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
}

void SetError(std::wstring* error, std::wstring value) {
    if (error) *error = std::move(value);
}

std::wstring ReadText(const fs::path& path, const wchar_t* section, const wchar_t* key) {
    std::vector<wchar_t> buffer(32768);
    GetPrivateProfileStringW(section, key, L"", buffer.data(), static_cast<DWORD>(buffer.size()), path.c_str());
    return buffer.data();
}

unsigned long long ReadU64(const fs::path& path, const wchar_t* section, const wchar_t* key) {
    const std::wstring text = ReadText(path, section, key);
    if (text.empty()) return 0;
    wchar_t* end = nullptr;
    const unsigned long long value = _wcstoui64(text.c_str(), &end, 10);
    return end == text.c_str() ? 0 : value;
}

std::wstring SectionName(std::size_t index) {
    wchar_t section[40]{};
    swprintf_s(section, L"Assignment.%04zu", index);
    return section;
}

bool SameId(std::wstring_view a, std::wstring_view b) noexcept {
    if (a.empty() || b.empty()) return false;
    const std::wstring left(a);
    const std::wstring right(b);
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

} // namespace

WallpaperMonitorAssignments::WallpaperMonitorAssignments() : storagePath_(DefaultStoragePath()) {}
WallpaperMonitorAssignments::WallpaperMonitorAssignments(fs::path storagePath) : storagePath_(std::move(storagePath)) {}

bool WallpaperMonitorAssignments::Load(std::wstring* error) {
    SetError(error, L"");
    items_.clear();
    std::error_code ec;
    if (!fs::exists(storagePath_, ec)) return true;

    const int count = std::clamp(GetPrivateProfileIntW(L"Assignments", L"Count", 0, storagePath_.c_str()), 0, 1024);
    for (int i = 0; i < count; ++i) {
        const std::wstring section = SectionName(static_cast<std::size_t>(i));
        MonitorWallpaperAssignment item;
        item.monitorId = ReadText(storagePath_, section.c_str(), L"MonitorId");
        item.wallpaperId = ReadText(storagePath_, section.c_str(), L"WallpaperId");
        item.lastFriendlyName = ReadText(storagePath_, section.c_str(), L"FriendlyName");
        item.lastSeenUnixSeconds = ReadU64(storagePath_, section.c_str(), L"LastSeen");
        if (item.monitorId.empty() || item.wallpaperId.empty()) continue;
        if (FindIndex(item.monitorId)) continue;
        items_.push_back(std::move(item));
    }
    return true;
}

bool WallpaperMonitorAssignments::Save(std::wstring* error) const {
    SetError(error, L"");
    std::error_code ec;
    fs::create_directories(storagePath_.parent_path(), ec);
    if (ec) {
        SetError(error, L"无法创建显示器壁纸配置目录，error=" + std::to_wstring(ec.value()));
        return false;
    }

    fs::path temporary = storagePath_;
    temporary += L".tmp";
    DeleteFileW(temporary.c_str());

    const std::wstring count = std::to_wstring(items_.size());
    bool ok = WritePrivateProfileStringW(L"Assignments", L"Version", L"1", temporary.c_str()) != FALSE;
    ok = (WritePrivateProfileStringW(L"Assignments", L"Count", count.c_str(), temporary.c_str()) != FALSE) && ok;
    for (std::size_t i = 0; i < items_.size(); ++i) {
        const auto& item = items_[i];
        const std::wstring section = SectionName(i);
        const std::wstring seen = std::to_wstring(item.lastSeenUnixSeconds);
        ok = (WritePrivateProfileStringW(section.c_str(), L"MonitorId", item.monitorId.c_str(), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"WallpaperId", item.wallpaperId.c_str(), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"FriendlyName", item.lastFriendlyName.c_str(), temporary.c_str()) != FALSE) && ok;
        ok = (WritePrivateProfileStringW(section.c_str(), L"LastSeen", seen.c_str(), temporary.c_str()) != FALSE) && ok;
    }
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, temporary.c_str());
    if (!ok) {
        DeleteFileW(temporary.c_str());
        SetError(error, L"写入显示器壁纸配置失败");
        return false;
    }

    if (!MoveFileExW(temporary.c_str(), storagePath_.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const DWORD win32 = GetLastError();
        DeleteFileW(temporary.c_str());
        SetError(error, L"提交显示器壁纸配置失败，Win32=" + std::to_wstring(win32));
        return false;
    }
    return true;
}

bool WallpaperMonitorAssignments::Assign(const MonitorInfo& monitor, std::wstring wallpaperId, std::wstring* error) {
    return AssignById(StableMonitorKey(monitor), std::move(wallpaperId),
                      monitor.friendlyName.empty() ? monitor.deviceName : monitor.friendlyName, error);
}

bool WallpaperMonitorAssignments::AssignById(std::wstring monitorId, std::wstring wallpaperId,
                                             std::wstring friendlyName, std::wstring* error) {
    SetError(error, L"");
    if (monitorId.empty() || wallpaperId.empty()) {
        SetError(error, L"显示器 ID 和壁纸 ID 不能为空");
        return false;
    }
    if (const auto index = FindIndex(monitorId)) {
        auto& item = items_[*index];
        item.wallpaperId = std::move(wallpaperId);
        if (!friendlyName.empty()) item.lastFriendlyName = std::move(friendlyName);
        item.lastSeenUnixSeconds = NowUnixSeconds();
    } else {
        MonitorWallpaperAssignment item;
        item.monitorId = std::move(monitorId);
        item.wallpaperId = std::move(wallpaperId);
        item.lastFriendlyName = std::move(friendlyName);
        item.lastSeenUnixSeconds = NowUnixSeconds();
        items_.push_back(std::move(item));
    }
    return Save(error);
}

bool WallpaperMonitorAssignments::Clear(std::wstring_view monitorId, std::wstring* error) {
    SetError(error, L"");
    const auto index = FindIndex(monitorId);
    if (!index) return true;
    items_.erase(items_.begin() + static_cast<std::ptrdiff_t>(*index));
    return Save(error);
}

void WallpaperMonitorAssignments::TouchTopology(const MonitorTopology& topology) {
    const unsigned long long now = NowUnixSeconds();
    for (const auto& monitor : topology.monitors) {
        const auto index = FindIndex(StableMonitorKey(monitor));
        if (!index) continue;
        auto& item = items_[*index];
        item.lastSeenUnixSeconds = now;
        const std::wstring name = monitor.friendlyName.empty() ? monitor.deviceName : monitor.friendlyName;
        if (!name.empty()) item.lastFriendlyName = name;
    }
}

std::optional<std::wstring> WallpaperMonitorAssignments::WallpaperIdFor(std::wstring_view monitorId) const {
    if (const auto index = FindIndex(monitorId)) return items_[*index].wallpaperId;
    return std::nullopt;
}

std::optional<std::wstring> WallpaperMonitorAssignments::WallpaperIdFor(const MonitorInfo& monitor) const {
    return WallpaperIdFor(StableMonitorKey(monitor));
}

const std::vector<MonitorWallpaperAssignment>& WallpaperMonitorAssignments::Items() const noexcept {
    return items_;
}

std::vector<MonitorWallpaperAssignment> WallpaperMonitorAssignments::MissingFrom(const MonitorTopology& topology) const {
    std::vector<MonitorWallpaperAssignment> missing;
    for (const auto& item : items_) {
        if (!FindMonitorByStableId(topology, item.monitorId)) missing.push_back(item);
    }
    return missing;
}

const fs::path& WallpaperMonitorAssignments::StoragePath() const noexcept {
    return storagePath_;
}

std::optional<std::size_t> WallpaperMonitorAssignments::FindIndex(std::wstring_view monitorId) const noexcept {
    for (std::size_t i = 0; i < items_.size(); ++i) {
        if (SameId(items_[i].monitorId, monitorId)) return i;
    }
    return std::nullopt;
}

bool WallpaperMonitorAssignments::SelfTest() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-MonitorAssignments-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    const fs::path storage = root / L"assignments.ini";
    WallpaperMonitorAssignments assignments(storage);
    std::wstring error;
    bool ok = assignments.Load(&error);
    ok = ok && assignments.AssignById(L"monitor-A", L"wallpaper-one", L"Panel A", &error);
    ok = ok && assignments.AssignById(L"monitor-B", L"wallpaper-two", L"Panel B", &error);
    ok = ok && assignments.WallpaperIdFor(L"MONITOR-a") == std::optional<std::wstring>(L"wallpaper-one");

    MonitorTopology topology;
    topology.virtualBounds = {0, 0, 3840, 1080};
    topology.primaryBounds = {0, 0, 1920, 1080};
    topology.monitors = {
        {nullptr, {0, 0, 1920, 1080}, true, 96, 96, L"DISPLAY-A", L"monitor-A", L"Panel A"},
    };
    ok = ok && assignments.MissingFrom(topology).size() == 1;
    assignments.TouchTopology(topology);
    ok = ok && assignments.Save(&error);

    WallpaperMonitorAssignments reloaded(storage);
    ok = ok && reloaded.Load(&error);
    ok = ok && reloaded.WallpaperIdFor(L"monitor-A") == std::optional<std::wstring>(L"wallpaper-one");
    ok = ok && reloaded.WallpaperIdFor(L"monitor-B") == std::optional<std::wstring>(L"wallpaper-two");
    ok = ok && reloaded.Clear(L"monitor-A", &error);
    ok = ok && !reloaded.WallpaperIdFor(L"monitor-A").has_value();

    fs::remove_all(root, ec);
    return ok;
}

} // namespace turingdesk::wallpaper
