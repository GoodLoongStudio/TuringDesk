#pragma once

#include <windows.h>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "turingdesk/WallpaperPerformancePolicy.h"

namespace turingdesk::wallpaper {

enum class PlaylistOrder {
    Sequential,
    Random,
};

enum class AutomationTargetKind {
    Profile,
    Playlist,
};

struct WallpaperProfile {
    std::wstring id;
    std::wstring name;
    std::wstring wallpaperId;
    std::wstring layout{L"span"};
    std::wstring scale{L"cover"};
    float focalX{0.5f};
    float focalY{0.5f};
    int fpsCap{30};
    int throttleFps{15};
    PerformanceAction fullscreenAction{PerformanceAction::Pause};
    PerformanceAction maximizedAction{PerformanceAction::Throttle};
    PerformanceAction remoteSessionAction{PerformanceAction::Throttle};
    PerformanceAction batterySaverAction{PerformanceAction::Throttle};
    PerformanceAction lockedSessionAction{PerformanceAction::Stop};
    PerformanceAction idleAction{PerformanceAction::Throttle};
    DWORD idleThresholdSeconds{120};
    bool videoLoop{true};
    bool videoMuted{true};
    float videoVolume{0.0f};
    float videoRate{1.0f};
};

struct WallpaperPlaylist {
    std::wstring id;
    std::wstring name;
    std::vector<std::wstring> wallpaperIds;
    unsigned intervalSeconds{900};
    PlaylistOrder order{PlaylistOrder::Sequential};
    bool enabled{true};
    std::size_t cursor{};
    unsigned long long lastRotationUnixSeconds{};
};

struct WallpaperSchedule {
    std::wstring id;
    std::wstring name;
    bool enabled{true};
    unsigned dayMask{0x7f}; // bit 0 = Sunday ... bit 6 = Saturday
    int startMinute{};      // local minutes after midnight, inclusive
    int endMinute{1440};    // local minutes after midnight, exclusive
    AutomationTargetKind targetKind{AutomationTargetKind::Profile};
    std::wstring targetId;
};

enum class AutomationDecisionKind {
    None,
    ApplyWallpaper,
    ApplyProfile,
};

struct AutomationDecision {
    AutomationDecisionKind kind{AutomationDecisionKind::None};
    std::wstring targetId;
    std::wstring sourceId;
    std::wstring reason;
};

class WallpaperAutomationStore {
public:
    WallpaperAutomationStore();
    explicit WallpaperAutomationStore(std::filesystem::path storagePath);

    bool Load(std::wstring* error = nullptr);
    bool Save(std::wstring* error = nullptr) const;

    const std::vector<WallpaperProfile>& Profiles() const noexcept;
    const std::vector<WallpaperPlaylist>& Playlists() const noexcept;
    const std::vector<WallpaperSchedule>& Schedules() const noexcept;

    std::optional<WallpaperProfile> FindProfile(std::wstring_view id) const;
    std::optional<WallpaperPlaylist> FindPlaylist(std::wstring_view id) const;
    std::optional<WallpaperSchedule> FindSchedule(std::wstring_view id) const;

    bool UpsertProfile(WallpaperProfile profile, std::wstring* error = nullptr);
    bool UpsertPlaylist(WallpaperPlaylist playlist, std::wstring* error = nullptr);
    bool UpsertSchedule(WallpaperSchedule schedule, std::wstring* error = nullptr);
    bool RemoveProfile(std::wstring_view id, std::wstring* error = nullptr);
    bool RemovePlaylist(std::wstring_view id, std::wstring* error = nullptr);
    bool RemoveSchedule(std::wstring_view id, std::wstring* error = nullptr);

    bool SetEnabled(bool enabled, std::wstring* error = nullptr);
    bool Enabled() const noexcept;
    bool SetActivePlaylist(std::wstring playlistId, std::wstring* error = nullptr);
    const std::wstring& ActivePlaylistId() const noexcept;

    AutomationDecision Evaluate(const SYSTEMTIME& localTime, unsigned long long unixSeconds);
    AutomationDecision ForceNextPlaylist(std::wstring_view playlistId, unsigned long long unixSeconds);

    const std::wstring& LastMatchedScheduleId() const noexcept;
    const std::filesystem::path& StoragePath() const noexcept;

    static bool ScheduleMatches(const WallpaperSchedule& schedule, const SYSTEMTIME& localTime) noexcept;
    static std::wstring MakeId(std::wstring_view prefix);
    static bool SelfTest();

private:
    std::optional<std::size_t> ProfileIndex(std::wstring_view id) const noexcept;
    std::optional<std::size_t> PlaylistIndex(std::wstring_view id) const noexcept;
    std::optional<std::size_t> ScheduleIndex(std::wstring_view id) const noexcept;
    AutomationDecision NextPlaylistDecision(WallpaperPlaylist& playlist, unsigned long long unixSeconds, bool force);

    std::filesystem::path storagePath_;
    bool enabled_{true};
    std::wstring activePlaylistId_;
    std::wstring lastMatchedScheduleId_;
    std::vector<WallpaperProfile> profiles_;
    std::vector<WallpaperPlaylist> playlists_;
    std::vector<WallpaperSchedule> schedules_;
};

const wchar_t* PlaylistOrderKey(PlaylistOrder order) noexcept;
PlaylistOrder ParsePlaylistOrder(std::wstring_view value) noexcept;
const wchar_t* AutomationTargetKindKey(AutomationTargetKind kind) noexcept;
AutomationTargetKind ParseAutomationTargetKind(std::wstring_view value) noexcept;

} // namespace turingdesk::wallpaper
