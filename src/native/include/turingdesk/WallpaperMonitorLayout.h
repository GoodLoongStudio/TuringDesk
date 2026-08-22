#pragma once

#include <windows.h>
#include <string>
#include <string_view>
#include <vector>

namespace turingdesk::wallpaper {

enum class LayoutMode {
    Span,
    Clone,
    Independent,
    PrimaryOnly,
};

struct MonitorInfo {
    HMONITOR handle{};
    RECT desktopRect{};
    bool primary{};
    UINT dpiX{96};
    UINT dpiY{96};
    std::wstring deviceName;
    std::wstring stableId;
    std::wstring friendlyName;
};

struct MonitorTopology {
    std::vector<MonitorInfo> monitors;
    RECT virtualBounds{};
    RECT primaryBounds{};

    bool Valid() const noexcept;
};

MonitorTopology QueryMonitorTopology();
const MonitorInfo* FindMonitorByStableId(const MonitorTopology& topology, std::wstring_view stableId) noexcept;
std::wstring StableMonitorKey(const MonitorInfo& monitor);
LayoutMode ParseLayoutMode(const std::wstring& value) noexcept;
const wchar_t* LayoutModeKey(LayoutMode mode) noexcept;
const wchar_t* LayoutModeDisplayName(LayoutMode mode) noexcept;
RECT HostDesktopBounds(const MonitorTopology& topology, LayoutMode mode) noexcept;
std::vector<RECT> DrawRegionsInHost(const MonitorTopology& topology, LayoutMode mode);
RECT DesktopRectToParentClient(HWND parent, const RECT& desktopRect) noexcept;
std::wstring DescribeMonitorTopology(const MonitorTopology& topology);
bool SelfTestMonitorLayoutGeometry() noexcept;

} // namespace turingdesk::wallpaper
