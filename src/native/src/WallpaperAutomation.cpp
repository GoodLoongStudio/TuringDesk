#include "turingdesk/WallpaperAutomation.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <functional>
#include <system_error>
#include <utility>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr unsigned kAllDaysMask = 0x7f;
constexpr unsigned kMinPlaylistIntervalSeconds = 30;
constexpr unsigned kMaxPlaylistIntervalSeconds = 7 * 24 * 60 * 60;

fs::path DefaultStoragePath() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    const fs::path root = (length > 0 && length < std::size(local))
        ? fs::path(local)
        : fs::temp_directory_path();
    return root / L"TuringDesk" / L"WallpaperLibrary" / L"automation.ini";
}

void SetError(std::wstring* error, std::wstring value) {
    if (error) *error = std::move(value);
}

bool SameId(std::wstring_view a, std::wstring_view b) noexcept {
    if (a.empty() || b.empty()) return false;
    const std::wstring left(a);
    const std::wstring right(b);
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

std::wstring SectionName(const wchar_t* prefix, std::size_t index) {
    wchar_t section[64]{};
    swprintf_s(section, L"%s.%04zu", prefix, index);
    return section;
}

std::wstring EntryKey(std::size_t index) {
    wchar_t key[40]{};
    swprintf_s(key, L"Entry.%04zu", index);
    return key;
}

std::wstring ReadText(const fs::path& path, const wchar_t* section, const wchar_t* key,
                      const wchar_t* fallback = L"") {
    std::vector<wchar_t> buffer(32768);
    GetPrivateProfileStringW(section, key, fallback, buffer.data(), static_cast<DWORD>(buffer.size()), path.c_str());
    return buffer.data();
}

unsigned ReadUnsigned(const fs::path& path, const wchar_t* section, const wchar_t* key, unsigned fallback) {
    const UINT value = GetPrivateProfileIntW(section, key, fallback, path.c_str());
    return static_cast<unsigned>(value);
}

unsigned long long ReadU64(const fs::path& path, const wchar_t* section, const wchar_t* key,
                           unsigned long long fallback = 0) {
    const std::wstring text = ReadText(path, section, key);
    if (text.empty()) return fallback;
    wchar_t* end = nullptr;
    const unsigned long long value = _wcstoui64(text.c_str(), &end, 10);
    return end == text.c_str() ? fallback : value;
}

float ReadFloat(const fs::path& path, const wchar_t* section, const wchar_t* key, float fallback) {
    wchar_t fallbackText[64]{};
    swprintf_s(fallbackText, L"%.4f", fallback);
    const std::wstring text = ReadText(path, section, key, fallbackText);
    wchar_t* end = nullptr;
    const float value = std::wcstof(text.c_str(), &end);
    return end == text.c_str() ? fallback : value;
}

bool WriteText(const fs::path& path, const wchar_t* section, const wchar_t* key, const std::wstring& value) {
    return WritePrivateProfileStringW(section, key, value.c_str(), path.c_str()) != FALSE;
}

bool WriteText(const fs::path& path, const wchar_t* section, const wchar_t* key, const wchar_t* value) {
    return WritePrivateProfileStringW(section, key, value, path.c_str()) != FALSE;
}

bool WriteUnsigned(const fs::path& path, const wchar_t* section, const wchar_t* key, unsigned value) {
    return WriteText(path, section, key, std::to_wstring(value));
}

bool WriteU64(const fs::path& path, const wchar_t* section, const wchar_t* key, unsigned long long value) {
    return WriteText(path, section, key, std::to_wstring(value));
}

bool WriteFloat(const fs::path& path, const wchar_t* section, const wchar_t* key, float value) {
    wchar_t text[64]{};
    swprintf_s(text, L"%.4f", value);
    return WriteText(path, section, key, text);
}

unsigned ReadCount(const fs::path& path, const wchar_t* key) {
    const UINT raw = GetPrivateProfileIntW(L"Automation", key, 0, path.c_str());
    return std::clamp<UINT>(raw, 0U, 1024U);
}

unsigned PreviousDay(unsigned day) noexcept {
    return day == 0 ? 6U : day - 1U;
}

bool DayEnabled(unsigned dayMask, unsigned day) noexcept {
    if (day > 6) return false;
    return (dayMask & (1U << day)) != 0;
}

std::size_t DeterministicIndex(const WallpaperPlaylist& playlist, unsigned long long unixSeconds) {
    std::size_t seed = std::hash<std::wstring>{}(playlist.id);
    seed ^= static_cast<std::size_t>(unixSeconds + 0x9e3779b97f4a7c15ULL + (seed << 6U) + (seed >> 2U));
    seed ^= playlist.cursor + 0x9e3779b9U + (seed << 6U) + (seed >> 2U);
    return playlist.wallpaperIds.empty() ? 0 : seed % playlist.wallpaperIds.size();
}

} // namespace

const wchar_t* PlaylistOrderKey(PlaylistOrder order) noexcept {
    return order == PlaylistOrder::Random ? L"random" : L"sequential";
}

PlaylistOrder ParsePlaylistOrder(std::wstring_view value) noexcept {
    const std::wstring text(value);
    return _wcsicmp(text.c_str(), L"random") == 0 ? PlaylistOrder::Random : PlaylistOrder::Sequential;
}

const wchar_t* AutomationTargetKindKey(AutomationTargetKind kind) noexcept {
    return kind == AutomationTargetKind::Playlist ? L"playlist" : L"profile";
}

AutomationTargetKind ParseAutomationTargetKind(std::wstring_view value) noexcept {
    const std::wstring text(value);
    return _wcsicmp(text.c_str(), L"playlist") == 0 ? AutomationTargetKind::Playlist : AutomationTargetKind::Profile;
}

WallpaperAutomationStore::WallpaperAutomationStore() : storagePath_(DefaultStoragePath()) {}
WallpaperAutomationStore::WallpaperAutomationStore(fs::path storagePath) : storagePath_(std::move(storagePath)) {}

bool WallpaperAutomationStore::Load(std::wstring* error) {
    SetError(error, L"");
    profiles_.clear();
    playlists_.clear();
    schedules_.clear();
    activePlaylistId_.clear();
    lastMatchedScheduleId_.clear();

    std::error_code ec;
    if (!fs::exists(storagePath_, ec)) return true;

    enabled_ = GetPrivateProfileIntW(L"Automation", L"Enabled", 1, storagePath_.c_str()) != 0;
    activePlaylistId_ = ReadText(storagePath_, L"Automation", L"ActivePlaylist");
    lastMatchedScheduleId_ = ReadText(storagePath_, L"Runtime", L"LastMatchedSchedule");

    const unsigned profileCount = ReadCount(storagePath_, L"ProfileCount");
    for (unsigned i = 0; i < profileCount; ++i) {
        const std::wstring section = SectionName(L"Profile", i);
        WallpaperProfile profile;
        profile.id = ReadText(storagePath_, section.c_str(), L"Id");
        profile.name = ReadText(storagePath_, section.c_str(), L"Name");
        profile.wallpaperId = ReadText(storagePath_, section.c_str(), L"WallpaperId");
        profile.layout = ReadText(storagePath_, section.c_str(), L"Layout", L"span");
        profile.scale = ReadText(storagePath_, section.c_str(), L"Scale", L"cover");
        profile.focalX = std::clamp(ReadFloat(storagePath_, section.c_str(), L"FocalX", 0.5f), 0.0f, 1.0f);
        profile.focalY = std::clamp(ReadFloat(storagePath_, section.c_str(), L"FocalY", 0.5f), 0.0f, 1.0f);
        profile.fpsCap = NormalizeFpsCap(static_cast<int>(ReadUnsigned(storagePath_, section.c_str(), L"FpsCap", 30)));
        profile.throttleFps = NormalizeFpsCap(static_cast<int>(ReadUnsigned(storagePath_, section.c_str(), L"ThrottleFps", 15)));
        profile.fullscreenAction = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"FullscreenAction", L"pause"));
        profile.maximizedAction = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"MaximizedAction", L"throttle"));
        profile.remoteSessionAction = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"RemoteSessionAction", L"throttle"));
        profile.batterySaverAction = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"BatterySaverAction", L"throttle"));
        profile.lockedSessionAction = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"LockedSessionAction", L"stop"));
        profile.idleAction = ParsePerformanceAction(ReadText(storagePath_, section.c_str(), L"IdleAction", L"throttle"));
        profile.idleThresholdSeconds = static_cast<DWORD>(std::clamp<unsigned>(
            ReadUnsigned(storagePath_, section.c_str(), L"IdleThresholdSeconds", 120), 30U, 3600U));
        profile.videoLoop = GetPrivateProfileIntW(section.c_str(), L"VideoLoop", 1, storagePath_.c_str()) != 0;
        profile.videoMuted = GetPrivateProfileIntW(section.c_str(), L"VideoMuted", 1, storagePath_.c_str()) != 0;
        profile.videoVolume = std::clamp(ReadFloat(storagePath_, section.c_str(), L"VideoVolume", 0.0f), 0.0f, 1.0f);
        profile.videoRate = std::clamp(ReadFloat(storagePath_, section.c_str(), L"VideoRate", 1.0f), 0.25f, 4.0f);
        if (profile.id.empty() || profile.wallpaperId.empty() || ProfileIndex(profile.id)) continue;
        if (profile.name.empty()) profile.name = profile.id;
        profiles_.push_back(std::move(profile));
    }

    const unsigned playlistCount = ReadCount(storagePath_, L"PlaylistCount");
    for (unsigned i = 0; i < playlistCount; ++i) {
        const std::wstring section = SectionName(L"Playlist", i);
        WallpaperPlaylist playlist;
        playlist.id = ReadText(storagePath_, section.c_str(), L"Id");
        playlist.name = ReadText(storagePath_, section.c_str(), L"Name");
        playlist.intervalSeconds = std::clamp<unsigned>(
            ReadUnsigned(storagePath_, section.c_str(), L"IntervalSeconds", 900),
            kMinPlaylistIntervalSeconds, kMaxPlaylistIntervalSeconds);
        playlist.order = ParsePlaylistOrder(ReadText(storagePath_, section.c_str(), L"Order", L"sequential"));
        playlist.enabled = GetPrivateProfileIntW(section.c_str(), L"Enabled", 1, storagePath_.c_str()) != 0;
        playlist.cursor = static_cast<std::size_t>(ReadU64(storagePath_, section.c_str(), L"Cursor", 0));
        playlist.lastRotationUnixSeconds = ReadU64(storagePath_, section.c_str(), L"LastRotation", 0);
        const unsigned entryCount = std::clamp<unsigned>(
            ReadUnsigned(storagePath_, section.c_str(), L"EntryCount", 0), 0U, 4096U);
        for (unsigned entry = 0; entry < entryCount; ++entry) {
            const std::wstring key = EntryKey(entry);
            const std::wstring wallpaperId = ReadText(storagePath_, section.c_str(), key.c_str());
            if (!wallpaperId.empty()) playlist.wallpaperIds.push_back(wallpaperId);
        }
        if (playlist.id.empty() || playlist.wallpaperIds.empty() || PlaylistIndex(playlist.id)) continue;
        if (playlist.name.empty()) playlist.name = playlist.id;
        playlists_.push_back(std::move(playlist));
    }

    const unsigned scheduleCount = ReadCount(storagePath_, L"ScheduleCount");
    for (unsigned i = 0; i < scheduleCount; ++i) {
        const std::wstring section = SectionName(L"Schedule", i);
        WallpaperSchedule schedule;
        schedule.id = ReadText(storagePath_, section.c_str(), L"Id");
        schedule.name = ReadText(storagePath_, section.c_str(), L"Name");
        schedule.enabled = GetPrivateProfileIntW(section.c_str(), L"Enabled", 1, storagePath_.c_str()) != 0;
        schedule.dayMask = ReadUnsigned(storagePath_, section.c_str(), L"DayMask", kAllDaysMask) & kAllDaysMask;
        schedule.startMinute = std::clamp(static_cast<int>(ReadUnsigned(storagePath_, section.c_str(), L"StartMinute", 0)), 0, 1439);
        schedule.endMinute = std::clamp(static_cast<int>(ReadUnsigned(storagePath_, section.c_str(), L"EndMinute", 1440)), 0, 1440);
        schedule.targetKind = ParseAutomationTargetKind(ReadText(storagePath_, section.c_str(), L"TargetKind", L"profile"));
        schedule.targetId = ReadText(storagePath_, section.c_str(), L"TargetId");
        if (schedule.id.empty() || schedule.targetId.empty() || schedule.dayMask == 0 || ScheduleIndex(schedule.id)) continue;
        if (schedule.name.empty()) schedule.name = schedule.id;
        schedules_.push_back(std::move(schedule));
    }
    return true;
}

bool WallpaperAutomationStore::Save(std::wstring* error) const {
    SetError(error, L"");
    std::error_code ec;
    fs::create_directories(storagePath_.parent_path(), ec);
    if (ec) {
        SetError(error, L"无法创建壁纸自动化目录，error=" + std::to_wstring(ec.value()));
        return false;
    }

    fs::path temporary = storagePath_;
    temporary += L".tmp";
    DeleteFileW(temporary.c_str());
    bool ok = true;
    ok = WriteText(temporary, L"Automation", L"Version", L"1") && ok;
    ok = WriteText(temporary, L"Automation", L"Enabled", enabled_ ? L"1" : L"0") && ok;
    ok = WriteText(temporary, L"Automation", L"ActivePlaylist", activePlaylistId_) && ok;
    ok = WriteUnsigned(temporary, L"Automation", L"ProfileCount", static_cast<unsigned>(profiles_.size())) && ok;
    ok = WriteUnsigned(temporary, L"Automation", L"PlaylistCount", static_cast<unsigned>(playlists_.size())) && ok;
    ok = WriteUnsigned(temporary, L"Automation", L"ScheduleCount", static_cast<unsigned>(schedules_.size())) && ok;
    ok = WriteText(temporary, L"Runtime", L"LastMatchedSchedule", lastMatchedScheduleId_) && ok;

    for (std::size_t i = 0; i < profiles_.size(); ++i) {
        const auto& profile = profiles_[i];
        const std::wstring section = SectionName(L"Profile", i);
        ok = WriteText(temporary, section.c_str(), L"Id", profile.id) && ok;
        ok = WriteText(temporary, section.c_str(), L"Name", profile.name) && ok;
        ok = WriteText(temporary, section.c_str(), L"WallpaperId", profile.wallpaperId) && ok;
        ok = WriteText(temporary, section.c_str(), L"Layout", profile.layout) && ok;
        ok = WriteText(temporary, section.c_str(), L"Scale", profile.scale) && ok;
        ok = WriteFloat(temporary, section.c_str(), L"FocalX", profile.focalX) && ok;
        ok = WriteFloat(temporary, section.c_str(), L"FocalY", profile.focalY) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"FpsCap", static_cast<unsigned>(profile.fpsCap)) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"ThrottleFps", static_cast<unsigned>(profile.throttleFps)) && ok;
        ok = WriteText(temporary, section.c_str(), L"FullscreenAction", PerformanceActionKey(profile.fullscreenAction)) && ok;
        ok = WriteText(temporary, section.c_str(), L"MaximizedAction", PerformanceActionKey(profile.maximizedAction)) && ok;
        ok = WriteText(temporary, section.c_str(), L"RemoteSessionAction", PerformanceActionKey(profile.remoteSessionAction)) && ok;
        ok = WriteText(temporary, section.c_str(), L"BatterySaverAction", PerformanceActionKey(profile.batterySaverAction)) && ok;
        ok = WriteText(temporary, section.c_str(), L"LockedSessionAction", PerformanceActionKey(profile.lockedSessionAction)) && ok;
        ok = WriteText(temporary, section.c_str(), L"IdleAction", PerformanceActionKey(profile.idleAction)) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"IdleThresholdSeconds", profile.idleThresholdSeconds) && ok;
        ok = WriteText(temporary, section.c_str(), L"VideoLoop", profile.videoLoop ? L"1" : L"0") && ok;
        ok = WriteText(temporary, section.c_str(), L"VideoMuted", profile.videoMuted ? L"1" : L"0") && ok;
        ok = WriteFloat(temporary, section.c_str(), L"VideoVolume", profile.videoVolume) && ok;
        ok = WriteFloat(temporary, section.c_str(), L"VideoRate", profile.videoRate) && ok;
    }

    for (std::size_t i = 0; i < playlists_.size(); ++i) {
        const auto& playlist = playlists_[i];
        const std::wstring section = SectionName(L"Playlist", i);
        ok = WriteText(temporary, section.c_str(), L"Id", playlist.id) && ok;
        ok = WriteText(temporary, section.c_str(), L"Name", playlist.name) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"IntervalSeconds", playlist.intervalSeconds) && ok;
        ok = WriteText(temporary, section.c_str(), L"Order", PlaylistOrderKey(playlist.order)) && ok;
        ok = WriteText(temporary, section.c_str(), L"Enabled", playlist.enabled ? L"1" : L"0") && ok;
        ok = WriteU64(temporary, section.c_str(), L"Cursor", static_cast<unsigned long long>(playlist.cursor)) && ok;
        ok = WriteU64(temporary, section.c_str(), L"LastRotation", playlist.lastRotationUnixSeconds) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"EntryCount", static_cast<unsigned>(playlist.wallpaperIds.size())) && ok;
        for (std::size_t entry = 0; entry < playlist.wallpaperIds.size(); ++entry) {
            const std::wstring key = EntryKey(entry);
            ok = WriteText(temporary, section.c_str(), key.c_str(), playlist.wallpaperIds[entry]) && ok;
        }
    }

    for (std::size_t i = 0; i < schedules_.size(); ++i) {
        const auto& schedule = schedules_[i];
        const std::wstring section = SectionName(L"Schedule", i);
        ok = WriteText(temporary, section.c_str(), L"Id", schedule.id) && ok;
        ok = WriteText(temporary, section.c_str(), L"Name", schedule.name) && ok;
        ok = WriteText(temporary, section.c_str(), L"Enabled", schedule.enabled ? L"1" : L"0") && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"DayMask", schedule.dayMask & kAllDaysMask) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"StartMinute", static_cast<unsigned>(std::clamp(schedule.startMinute, 0, 1439))) && ok;
        ok = WriteUnsigned(temporary, section.c_str(), L"EndMinute", static_cast<unsigned>(std::clamp(schedule.endMinute, 0, 1440))) && ok;
        ok = WriteText(temporary, section.c_str(), L"TargetKind", AutomationTargetKindKey(schedule.targetKind)) && ok;
        ok = WriteText(temporary, section.c_str(), L"TargetId", schedule.targetId) && ok;
    }

    WritePrivateProfileStringW(nullptr, nullptr, nullptr, temporary.c_str());
    if (!ok) {
        DeleteFileW(temporary.c_str());
        SetError(error, L"写入壁纸自动化配置失败");
        return false;
    }
    if (!MoveFileExW(temporary.c_str(), storagePath_.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const DWORD win32 = GetLastError();
        DeleteFileW(temporary.c_str());
        SetError(error, L"提交壁纸自动化配置失败，Win32=" + std::to_wstring(win32));
        return false;
    }
    return true;
}

const std::vector<WallpaperProfile>& WallpaperAutomationStore::Profiles() const noexcept { return profiles_; }
const std::vector<WallpaperPlaylist>& WallpaperAutomationStore::Playlists() const noexcept { return playlists_; }
const std::vector<WallpaperSchedule>& WallpaperAutomationStore::Schedules() const noexcept { return schedules_; }

std::optional<WallpaperProfile> WallpaperAutomationStore::FindProfile(std::wstring_view id) const {
    if (const auto index = ProfileIndex(id)) return profiles_[*index];
    return std::nullopt;
}

std::optional<WallpaperPlaylist> WallpaperAutomationStore::FindPlaylist(std::wstring_view id) const {
    if (const auto index = PlaylistIndex(id)) return playlists_[*index];
    return std::nullopt;
}

std::optional<WallpaperSchedule> WallpaperAutomationStore::FindSchedule(std::wstring_view id) const {
    if (const auto index = ScheduleIndex(id)) return schedules_[*index];
    return std::nullopt;
}

bool WallpaperAutomationStore::UpsertProfile(WallpaperProfile profile, std::wstring* error) {
    SetError(error, L"");
    if (profile.id.empty()) profile.id = MakeId(L"profile");
    if (profile.name.empty()) profile.name = profile.id;
    if (profile.wallpaperId.empty()) {
        SetError(error, L"Profile 必须引用一个壁纸库项目");
        return false;
    }
    profile.focalX = std::clamp(profile.focalX, 0.0f, 1.0f);
    profile.focalY = std::clamp(profile.focalY, 0.0f, 1.0f);
    profile.fpsCap = NormalizeFpsCap(profile.fpsCap);
    profile.throttleFps = NormalizeFpsCap(profile.throttleFps);
    profile.idleThresholdSeconds = static_cast<DWORD>(std::clamp<DWORD>(profile.idleThresholdSeconds, 30U, 3600U));
    profile.videoVolume = std::clamp(profile.videoVolume, 0.0f, 1.0f);
    profile.videoRate = std::clamp(profile.videoRate, 0.25f, 4.0f);
    if (const auto index = ProfileIndex(profile.id)) profiles_[*index] = std::move(profile);
    else profiles_.push_back(std::move(profile));
    return Save(error);
}

bool WallpaperAutomationStore::UpsertPlaylist(WallpaperPlaylist playlist, std::wstring* error) {
    SetError(error, L"");
    if (playlist.id.empty()) playlist.id = MakeId(L"playlist");
    if (playlist.name.empty()) playlist.name = playlist.id;
    playlist.intervalSeconds = std::clamp<unsigned>(playlist.intervalSeconds,
                                                     kMinPlaylistIntervalSeconds,
                                                     kMaxPlaylistIntervalSeconds);
    playlist.wallpaperIds.erase(
        std::remove_if(playlist.wallpaperIds.begin(), playlist.wallpaperIds.end(),
                       [](const std::wstring& id) { return id.empty(); }),
        playlist.wallpaperIds.end());
    if (playlist.wallpaperIds.empty()) {
        SetError(error, L"Playlist 至少需要一个壁纸项目");
        return false;
    }
    if (const auto index = PlaylistIndex(playlist.id)) playlists_[*index] = std::move(playlist);
    else playlists_.push_back(std::move(playlist));
    return Save(error);
}

bool WallpaperAutomationStore::UpsertSchedule(WallpaperSchedule schedule, std::wstring* error) {
    SetError(error, L"");
    if (schedule.id.empty()) schedule.id = MakeId(L"schedule");
    if (schedule.name.empty()) schedule.name = schedule.id;
    schedule.dayMask &= kAllDaysMask;
    schedule.startMinute = std::clamp(schedule.startMinute, 0, 1439);
    schedule.endMinute = std::clamp(schedule.endMinute, 0, 1440);
    if (schedule.dayMask == 0 || schedule.targetId.empty()) {
        SetError(error, L"Schedule 必须包含星期范围和目标");
        return false;
    }
    if (const auto index = ScheduleIndex(schedule.id)) schedules_[*index] = std::move(schedule);
    else schedules_.push_back(std::move(schedule));
    return Save(error);
}

bool WallpaperAutomationStore::RemoveProfile(std::wstring_view id, std::wstring* error) {
    if (const auto index = ProfileIndex(id)) profiles_.erase(profiles_.begin() + static_cast<std::ptrdiff_t>(*index));
    schedules_.erase(std::remove_if(schedules_.begin(), schedules_.end(), [&](const WallpaperSchedule& schedule) {
        return schedule.targetKind == AutomationTargetKind::Profile && SameId(schedule.targetId, id);
    }), schedules_.end());
    return Save(error);
}

bool WallpaperAutomationStore::RemovePlaylist(std::wstring_view id, std::wstring* error) {
    if (const auto index = PlaylistIndex(id)) playlists_.erase(playlists_.begin() + static_cast<std::ptrdiff_t>(*index));
    if (SameId(activePlaylistId_, id)) activePlaylistId_.clear();
    schedules_.erase(std::remove_if(schedules_.begin(), schedules_.end(), [&](const WallpaperSchedule& schedule) {
        return schedule.targetKind == AutomationTargetKind::Playlist && SameId(schedule.targetId, id);
    }), schedules_.end());
    return Save(error);
}

bool WallpaperAutomationStore::RemoveSchedule(std::wstring_view id, std::wstring* error) {
    if (const auto index = ScheduleIndex(id)) schedules_.erase(schedules_.begin() + static_cast<std::ptrdiff_t>(*index));
    if (SameId(lastMatchedScheduleId_, id)) lastMatchedScheduleId_.clear();
    return Save(error);
}

bool WallpaperAutomationStore::SetEnabled(bool enabled, std::wstring* error) {
    enabled_ = enabled;
    if (!enabled_) lastMatchedScheduleId_.clear();
    return Save(error);
}

bool WallpaperAutomationStore::Enabled() const noexcept { return enabled_; }

bool WallpaperAutomationStore::SetActivePlaylist(std::wstring playlistId, std::wstring* error) {
    if (!playlistId.empty() && !PlaylistIndex(playlistId)) {
        SetError(error, L"找不到要激活的 Playlist");
        return false;
    }
    activePlaylistId_ = std::move(playlistId);
    return Save(error);
}

const std::wstring& WallpaperAutomationStore::ActivePlaylistId() const noexcept { return activePlaylistId_; }

bool WallpaperAutomationStore::ScheduleMatches(const WallpaperSchedule& schedule, const SYSTEMTIME& localTime) noexcept {
    if (!schedule.enabled || schedule.dayMask == 0 || schedule.targetId.empty()) return false;
    const int minute = std::clamp(static_cast<int>(localTime.wHour) * 60 + static_cast<int>(localTime.wMinute), 0, 1439);
    const unsigned day = std::min<unsigned>(localTime.wDayOfWeek, 6U);
    const int start = std::clamp(schedule.startMinute, 0, 1439);
    const int end = std::clamp(schedule.endMinute, 0, 1440);

    if (start == end) return DayEnabled(schedule.dayMask, day);
    if (start < end) return DayEnabled(schedule.dayMask, day) && minute >= start && minute < end;
    if (minute >= start) return DayEnabled(schedule.dayMask, day);
    return minute < end && DayEnabled(schedule.dayMask, PreviousDay(day));
}

AutomationDecision WallpaperAutomationStore::NextPlaylistDecision(WallpaperPlaylist& playlist,
                                                                    unsigned long long unixSeconds,
                                                                    bool force) {
    AutomationDecision decision;
    if (!playlist.enabled || playlist.wallpaperIds.empty()) return decision;
    const unsigned interval = std::clamp<unsigned>(playlist.intervalSeconds,
                                                   kMinPlaylistIntervalSeconds,
                                                   kMaxPlaylistIntervalSeconds);
    if (!force && playlist.lastRotationUnixSeconds != 0 &&
        unixSeconds >= playlist.lastRotationUnixSeconds &&
        unixSeconds - playlist.lastRotationUnixSeconds < interval) {
        return decision;
    }

    std::size_t index = 0;
    if (playlist.order == PlaylistOrder::Random) {
        index = DeterministicIndex(playlist, unixSeconds);
        if (playlist.wallpaperIds.size() > 1 && playlist.cursor < playlist.wallpaperIds.size() && index == playlist.cursor)
            index = (index + 1) % playlist.wallpaperIds.size();
        playlist.cursor = index;
    } else {
        index = playlist.cursor % playlist.wallpaperIds.size();
        playlist.cursor = (index + 1) % playlist.wallpaperIds.size();
    }
    playlist.lastRotationUnixSeconds = unixSeconds;
    decision.kind = AutomationDecisionKind::ApplyWallpaper;
    decision.targetId = playlist.wallpaperIds[index];
    decision.sourceId = playlist.id;
    decision.reason = L"Playlist · " + playlist.name;
    return decision;
}

AutomationDecision WallpaperAutomationStore::Evaluate(const SYSTEMTIME& localTime, unsigned long long unixSeconds) {
    AutomationDecision decision;
    if (!enabled_) return decision;

    WallpaperSchedule* matched = nullptr;
    for (auto& schedule : schedules_) {
        if (ScheduleMatches(schedule, localTime)) {
            matched = &schedule;
            break;
        }
    }

    if (matched) {
        const bool newlyMatched = !SameId(lastMatchedScheduleId_, matched->id);
        lastMatchedScheduleId_ = matched->id;
        if (matched->targetKind == AutomationTargetKind::Profile) {
            if (newlyMatched && ProfileIndex(matched->targetId)) {
                decision.kind = AutomationDecisionKind::ApplyProfile;
                decision.targetId = matched->targetId;
                decision.sourceId = matched->id;
                decision.reason = L"Schedule · " + matched->name;
                Save(nullptr);
                return decision;
            }
        } else if (const auto index = PlaylistIndex(matched->targetId)) {
            decision = NextPlaylistDecision(playlists_[*index], unixSeconds, newlyMatched);
            if (decision.kind != AutomationDecisionKind::None) decision.reason = L"Schedule · " + matched->name + L" → " + decision.reason;
            Save(nullptr);
            return decision;
        }
        if (newlyMatched) Save(nullptr);
        return decision;
    }

    const bool scheduleJustEnded = !lastMatchedScheduleId_.empty();
    if (scheduleJustEnded) lastMatchedScheduleId_.clear();
    if (const auto index = PlaylistIndex(activePlaylistId_)) {
        decision = NextPlaylistDecision(playlists_[*index], unixSeconds, scheduleJustEnded);
        if (decision.kind != AutomationDecisionKind::None || scheduleJustEnded) Save(nullptr);
        return decision;
    }
    if (scheduleJustEnded) Save(nullptr);
    return decision;
}

AutomationDecision WallpaperAutomationStore::ForceNextPlaylist(std::wstring_view playlistId,
                                                                 unsigned long long unixSeconds) {
    if (const auto index = PlaylistIndex(playlistId)) {
        auto decision = NextPlaylistDecision(playlists_[*index], unixSeconds, true);
        if (decision.kind != AutomationDecisionKind::None) Save(nullptr);
        return decision;
    }
    return {};
}

const std::wstring& WallpaperAutomationStore::LastMatchedScheduleId() const noexcept {
    return lastMatchedScheduleId_;
}

const fs::path& WallpaperAutomationStore::StoragePath() const noexcept { return storagePath_; }

std::optional<std::size_t> WallpaperAutomationStore::ProfileIndex(std::wstring_view id) const noexcept {
    for (std::size_t i = 0; i < profiles_.size(); ++i) if (SameId(profiles_[i].id, id)) return i;
    return std::nullopt;
}

std::optional<std::size_t> WallpaperAutomationStore::PlaylistIndex(std::wstring_view id) const noexcept {
    for (std::size_t i = 0; i < playlists_.size(); ++i) if (SameId(playlists_[i].id, id)) return i;
    return std::nullopt;
}

std::optional<std::size_t> WallpaperAutomationStore::ScheduleIndex(std::wstring_view id) const noexcept {
    for (std::size_t i = 0; i < schedules_.size(); ++i) if (SameId(schedules_[i].id, id)) return i;
    return std::nullopt;
}

std::wstring WallpaperAutomationStore::MakeId(std::wstring_view prefix) {
    std::wstring result(prefix.empty() ? L"item" : prefix);
    result += L"-" + std::to_wstring(GetCurrentProcessId());
    result += L"-" + std::to_wstring(GetTickCount64());
    return result;
}

bool WallpaperAutomationStore::SelfTest() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-Automation-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    WallpaperAutomationStore store(root / L"automation.ini");
    std::wstring error;
    bool ok = store.Load(&error);

    WallpaperProfile day;
    day.id = L"profile-day";
    day.name = L"Day";
    day.wallpaperId = L"wallpaper-day";
    day.layout = L"clone";
    day.fpsCap = 60;
    ok = ok && store.UpsertProfile(day, &error);

    WallpaperPlaylist playlist;
    playlist.id = L"playlist-main";
    playlist.name = L"Main";
    playlist.wallpaperIds = {L"wallpaper-a", L"wallpaper-b"};
    playlist.intervalSeconds = 60;
    playlist.order = PlaylistOrder::Sequential;
    ok = ok && store.UpsertPlaylist(playlist, &error);
    ok = ok && store.SetActivePlaylist(playlist.id, &error);

    WallpaperSchedule work;
    work.id = L"schedule-work";
    work.name = L"Work hours";
    work.dayMask = 1U << 1U; // Monday
    work.startMinute = 8 * 60;
    work.endMinute = 10 * 60;
    work.targetKind = AutomationTargetKind::Profile;
    work.targetId = day.id;
    ok = ok && store.UpsertSchedule(work, &error);

    SYSTEMTIME monday{};
    monday.wDayOfWeek = 1;
    monday.wHour = 9;
    auto decision = store.Evaluate(monday, 1000);
    ok = ok && decision.kind == AutomationDecisionKind::ApplyProfile && decision.targetId == day.id;
    decision = store.Evaluate(monday, 1010);
    ok = ok && decision.kind == AutomationDecisionKind::None;

    SYSTEMTIME tuesday{};
    tuesday.wDayOfWeek = 2;
    tuesday.wHour = 12;
    decision = store.Evaluate(tuesday, 1020);
    ok = ok && decision.kind == AutomationDecisionKind::ApplyWallpaper && decision.targetId == L"wallpaper-a";
    decision = store.Evaluate(tuesday, 1050);
    ok = ok && decision.kind == AutomationDecisionKind::None;
    decision = store.Evaluate(tuesday, 1081);
    ok = ok && decision.kind == AutomationDecisionKind::ApplyWallpaper && decision.targetId == L"wallpaper-b";

    WallpaperSchedule overnight;
    overnight.id = L"schedule-night";
    overnight.name = L"Friday night";
    overnight.dayMask = 1U << 5U; // Friday
    overnight.startMinute = 22 * 60;
    overnight.endMinute = 2 * 60;
    overnight.targetKind = AutomationTargetKind::Profile;
    overnight.targetId = day.id;
    ok = ok && store.UpsertSchedule(overnight, &error);
    SYSTEMTIME saturdayNight{};
    saturdayNight.wDayOfWeek = 6;
    saturdayNight.wHour = 1;
    ok = ok && ScheduleMatches(overnight, saturdayNight);

    WallpaperAutomationStore reloaded(root / L"automation.ini");
    ok = ok && reloaded.Load(&error);
    ok = ok && reloaded.Profiles().size() == 1;
    ok = ok && reloaded.Playlists().size() == 1;
    ok = ok && reloaded.Schedules().size() == 2;
    ok = ok && reloaded.ActivePlaylistId() == L"playlist-main";
    ok = ok && reloaded.FindProfile(L"PROFILE-DAY").has_value();

    fs::remove_all(root, ec);
    return ok;
}

} // namespace turingdesk::wallpaper
