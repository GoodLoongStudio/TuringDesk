#pragma once

#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "turingdesk/WallpaperMonitorLayout.h"

namespace turingdesk::wallpaper {

struct MonitorWallpaperAssignment {
    std::wstring monitorId;
    std::wstring wallpaperId;
    std::wstring lastFriendlyName;
    unsigned long long lastSeenUnixSeconds{};
};

class WallpaperMonitorAssignments {
public:
    WallpaperMonitorAssignments();
    explicit WallpaperMonitorAssignments(std::filesystem::path storagePath);

    bool Load(std::wstring* error = nullptr);
    bool Save(std::wstring* error = nullptr) const;

    bool Assign(const MonitorInfo& monitor, std::wstring wallpaperId, std::wstring* error = nullptr);
    bool AssignById(std::wstring monitorId, std::wstring wallpaperId,
                    std::wstring friendlyName = {}, std::wstring* error = nullptr);
    bool Clear(std::wstring_view monitorId, std::wstring* error = nullptr);
    void TouchTopology(const MonitorTopology& topology);

    std::optional<std::wstring> WallpaperIdFor(std::wstring_view monitorId) const;
    std::optional<std::wstring> WallpaperIdFor(const MonitorInfo& monitor) const;
    const std::vector<MonitorWallpaperAssignment>& Items() const noexcept;

    std::vector<MonitorWallpaperAssignment> MissingFrom(const MonitorTopology& topology) const;
    const std::filesystem::path& StoragePath() const noexcept;

    static bool SelfTest();

private:
    std::optional<std::size_t> FindIndex(std::wstring_view monitorId) const noexcept;

    std::filesystem::path storagePath_;
    std::vector<MonitorWallpaperAssignment> items_;
};

} // namespace turingdesk::wallpaper
